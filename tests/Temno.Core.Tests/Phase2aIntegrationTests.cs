using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Temno.Core.IO;
using Temno.Core.Models;
using Temno.Core.Project;
using Temno.Core.Session;
using Temno.Mcp;
using Xunit;

namespace Temno.Core.Tests;

[Collection("EnvRegistry")]
public class Phase2aIntegrationTests : IDisposable
{
    private static readonly byte[] ValidPngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    private readonly string _tempDir;
    private readonly string _registryPath;
    private readonly string _projectsDir;

    public Phase2aIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "TemnoIntegrationTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _projectsDir = Path.Combine(_tempDir, "Projects");
        Directory.CreateDirectory(_projectsDir);
        _registryPath = Path.Combine(_tempDir, "project-registry.json");

        Environment.SetEnvironmentVariable("TEMNO_PROJECTS_DIR", _projectsDir);
        Environment.SetEnvironmentVariable("TEMNO_REGISTRY_PATH", _registryPath);
    }

    public void Dispose()
    {
        ProjectHub.ActiveSession = null;
        Environment.SetEnvironmentVariable("TEMNO_PROJECTS_DIR", null);
        Environment.SetEnvironmentVariable("TEMNO_REGISTRY_PATH", null);
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore clean up errors
        }
    }

    [Fact]
    public void TestPhase2aWorkflow()
    {
        // 1. new_project
        var newProjRes = ToolExecutor.Execute("new_project", new JsonObject
        {
            ["name"] = "MyIntegrationTestProject",
            ["fps"] = 30,
            ["aspectRatio"] = "16:9",
            ["quality"] = "1080p"
        });

        Assert.False(newProjRes.IsError);
        var snapshotNode = JsonNode.Parse(newProjRes.Content[0].Text!);
        Assert.NotNull(snapshotNode);
        Assert.Equal("created", snapshotNode["status"]?.GetValue<string>());
        Assert.Equal("MyIntegrationTestProject", snapshotNode["name"]?.GetValue<string>());

        var activeSession = ProjectHub.ActiveSession;
        Assert.NotNull(activeSession);
        Assert.NotNull(activeSession.PackagePath);
        Assert.Contains("MyIntegrationTestProject", activeSession.PackagePath);

        // 2. import_media
        var dummyVideoPath = Path.Combine(_tempDir, "test_image.png");
        File.WriteAllBytes(dummyVideoPath, ValidPngBytes);

        var importRes = ToolExecutor.Execute("import_media", new JsonObject
        {
            ["path"] = dummyVideoPath
        });
        Assert.False(importRes.IsError);
        var mediaEntry = JsonNode.Parse(importRes.Content[0].Text!);
        Assert.NotNull(mediaEntry);
        var mediaId = mediaEntry["id"]?.GetValue<string>();
        Assert.NotNull(mediaId);

        // 3. add_clips
        var addRes = ToolExecutor.Execute("add_clips", new JsonObject
        {
            ["entries"] = new JsonArray
            {
                new JsonObject
                {
                    ["mediaRef"] = mediaId,
                    ["trackIndex"] = 0,
                    ["startFrame"] = 10,
                    ["endFrame"] = 60
                }
            }
        });
        Assert.False(addRes.IsError);
        var addResultNode = JsonNode.Parse(addRes.Content[0].Text!);
        Assert.NotNull(addResultNode);
        Assert.Equal("success", addResultNode["status"]?.GetValue<string>());

        // 4. get_timeline (verify clip added)
        var getTimelineRes = ToolExecutor.Execute("get_timeline", []);
        Assert.False(getTimelineRes.IsError);
        var timelineNode = JsonNode.Parse(getTimelineRes.Content[0].Text!);
        Assert.NotNull(timelineNode);
        
        var tracks = timelineNode["tracks"]?.AsArray();
        Assert.NotNull(tracks);
        var firstTrack = tracks[0];
        Assert.NotNull(firstTrack);
        var clips = firstTrack["clips"]?.AsArray();
        Assert.NotNull(clips);
        Assert.Single(clips);

        var firstClip = clips[0];
        Assert.NotNull(firstClip);
        Assert.Equal(mediaId, firstClip["mediaRef"]?.GetValue<string>());
        var frames = firstClip["frames"]?.AsArray();
        Assert.NotNull(frames);
        Assert.Equal(10, frames[0]?.GetValue<int>());
        Assert.Equal(60, frames[1]?.GetValue<int>());

        // 5. undo
        var undoRes = ToolExecutor.Execute("undo", []);
        Assert.False(undoRes.IsError);

        // 6. get_timeline (verify empty again)
        var getTimelineRes2 = ToolExecutor.Execute("get_timeline", []);
        var timelineNode2 = JsonNode.Parse(getTimelineRes2.Content[0].Text!);
        Assert.NotNull(timelineNode2);
        var tracks2 = timelineNode2["tracks"]?.AsArray();
        Assert.NotNull(tracks2);
        var firstTrack2 = tracks2[0];
        Assert.NotNull(firstTrack2);
        var clips2 = firstTrack2["clips"]?.AsArray();
        if (clips2 != null)
        {
            Assert.Empty(clips2);
        }

        // 7. export_project — fcpxml mode writes an XML file without decoding media.
        var fcpxmlPath = Path.Combine(_tempDir, "export.fcpxml");
        var exportRes = ToolExecutor.Execute("export_project", new JsonObject
        {
            ["mode"] = "fcpxml",
            ["outputPath"] = fcpxmlPath
        });
        Assert.False(exportRes.IsError);
        var exportJson = JsonNode.Parse(exportRes.Content[0].Text!);
        Assert.NotNull(exportJson);
        Assert.Equal("success", exportJson["status"]?.GetValue<string>());
        Assert.True(File.Exists(fcpxmlPath));
        Assert.Contains("<fcpxml", File.ReadAllText(fcpxmlPath));

        // Self-contained palmier package export (WP-112). WP-193 strict-write: a legacy
        // .palmier output path is rewritten to .temno; the mode name stays "palmier".
        var palmierPath = Path.Combine(_tempDir, "copy.palmier");
        var palmierExport = ToolExecutor.Execute("export_project", new JsonObject
        {
            ["mode"] = "palmier",
            ["outputPath"] = palmierPath
        });
        Assert.False(palmierExport.IsError, palmierExport.Content?.FirstOrDefault()?.Text ?? "palmier export failed");
        var temnoPath = Path.Combine(_tempDir, "copy.temno");
        Assert.True(Directory.Exists(temnoPath) || Directory.Exists(temnoPath.TrimEnd('\\', '/')));
        Assert.True(File.Exists(Path.Combine(temnoPath, "project.json")));
    }

    [Fact]
    public void TestGetTimelineCompactionAndGenerationWiring()
    {
        // 1. Create a new project via ToolExecutor
        var newProjRes = ToolExecutor.Execute("new_project", new JsonObject
        {
            ["name"] = "CompactionTestProject",
            ["fps"] = 30
        });
        Assert.False(newProjRes.IsError);

        var session = ProjectHub.ActiveSession;
        Assert.NotNull(session);

        // 2. Clear pre-existing timeline clips to start clean
        var timeline = session.ActiveTimeline;
        foreach (var track in timeline.Tracks)
        {
            track.Clips.Clear();
        }

        // Add 2 tracks if not present
        while (timeline.Tracks.Count < 2)
        {
            timeline.Tracks.Add(new Track { Type = ClipType.video });
        }
        timeline.Tracks[0].Type = ClipType.video;
        timeline.Tracks[1].Type = ClipType.audio;

        // 3. Add clips to trigger Linked Audio Folding
        // Video clip on Track 0, Audio clip on Track 1
        var videoClip = new Clip
        {
            Id = "video_clip_1",
            MediaType = ClipType.video,
            SourceClipType = ClipType.video,
            MediaRef = "media_ref_1",
            StartFrame = 10,
            DurationFrames = 50,
            LinkGroupId = "link_group_1"
        };
        var audioClip = new Clip
        {
            Id = "audio_clip_1",
            MediaType = ClipType.audio,
            SourceClipType = ClipType.audio,
            MediaRef = "media_ref_1", // same mediaRef
            StartFrame = 10,
            DurationFrames = 50,
            LinkGroupId = "link_group_1",
            Volume = 0.8 // different volume, should show as deviation
        };
        timeline.Tracks[0].Clips.Add(videoClip);
        timeline.Tracks[1].Clips.Add(audioClip);

        // 4. Add keyframes to videoClip to test keyframe compaction
        // RotationTrack: non-constant keyframes -> should flatten
        var rotTrack = new KeyframeTrack<double>();
        rotTrack.Upsert(new Keyframe<double> { Frame = 0, Value = 0, InterpolationOut = Interpolation.linear });
        rotTrack.Upsert(new Keyframe<double> { Frame = 10, Value = 45, InterpolationOut = Interpolation.linear });
        videoClip.RotationTrack = rotTrack;

        // OpacityTrack: constant keyframe matching default (1.0) -> should be omitted
        var opTrack = new KeyframeTrack<double>();
        opTrack.Upsert(new Keyframe<double> { Frame = 0, Value = 1.0 });
        videoClip.OpacityTrack = opTrack;

        // VolumeTrack values are dB (not Clip.Volume's linear scalar). A constant non-zero
        // envelope must remain a keyframe response and must not alter the static volume.
        var volTrack = new KeyframeTrack<double>();
        volTrack.Upsert(new Keyframe<double> { Frame = 0, Value = 0.5 });
        videoClip.VolumeTrack = volTrack;

        // 5. Add color grade effect to videoClip
        // Effect of type "color.exposure" with ev param -> should be moved to unified "color" block
        var colorParams = new Dictionary<string, EffectParam>
        {
            ["ev"] = new EffectParam { Value = 1.5 }
        };
        var colorEffect = new Effect
        {
            Id = "color_effect_1",
            Type = "color.exposure",
            Params = colorParams
        };
        videoClip.Effects = new List<Effect> { colorEffect };

        // 6. Add 3 caption clips sharing a captionGroupId to test caption group folding
        // Add a subtitle track
        var subtitleTrack = new Track { Type = ClipType.text };
        timeline.Tracks.Add(subtitleTrack);
        
        var cap1 = new Clip { Id = "cap_1", MediaType = ClipType.text, CaptionGroupId = "sub_group_1", StartFrame = 100, DurationFrames = 30, TextContent = "Hello" };
        var cap2 = new Clip { Id = "cap_2", MediaType = ClipType.text, CaptionGroupId = "sub_group_1", StartFrame = 140, DurationFrames = 30, TextContent = "World" };
        var cap3 = new Clip { Id = "cap_3", MediaType = ClipType.text, CaptionGroupId = "sub_group_1", StartFrame = 180, DurationFrames = 30, TextContent = "Test" };
        subtitleTrack.Clips.Add(cap1);
        subtitleTrack.Clips.Add(cap2);
        subtitleTrack.Clips.Add(cap3);

        // Save session changes
        session.MarkDirty();
        session.Save();

        // 7. Execute get_timeline and verify compaction shapes
        var getTimelineRes = ToolExecutor.Execute("get_timeline", new JsonObject { ["captionDetail"] = false });
        Assert.False(getTimelineRes.IsError);
        var timelineJson = JsonNode.Parse(getTimelineRes.Content[0].Text!);
        Assert.NotNull(timelineJson);

        var tracksJson = timelineJson["tracks"]?.AsArray();
        Assert.NotNull(tracksJson);

        // Verify Video Track (Track 0)
        var videoTrackJson = tracksJson[0];
        Assert.NotNull(videoTrackJson);
        var videoClipsJson = videoTrackJson["clips"]?.AsArray();
        Assert.NotNull(videoClipsJson);
        Assert.Single(videoClipsJson);

        var compVideoClip = videoClipsJson[0];
        Assert.NotNull(compVideoClip);
        Assert.Equal("video_clip_1", compVideoClip["id"]?.GetValue<string>());

        // Verify Linked Audio Folding: Audio details embedded under the video clip
        var audioSummary = compVideoClip["audio"];
        Assert.NotNull(audioSummary);
        Assert.Equal(0.8, audioSummary["volume"]?.GetValue<double>());

        // Verify Keyframe Compaction
        var keyframesJson = compVideoClip["keyframes"];
        Assert.NotNull(keyframesJson);
        
        // Rotation (non-constant): list of [frame, value] rows
        var rotJson = keyframesJson["rotation"]?.AsArray();
        Assert.NotNull(rotJson);
        Assert.Equal(2, rotJson.Count);
        
        var kf0 = rotJson[0]?.AsArray();
        Assert.NotNull(kf0);
        Assert.Equal(0, kf0[0]?.GetValue<int>());
        Assert.Equal(0.0, kf0[1]?.GetValue<double>());

        var kf1 = rotJson[1]?.AsArray();
        Assert.NotNull(kf1);
        Assert.Equal(10, kf1[0]?.GetValue<int>());
        Assert.Equal(45.0, kf1[1]?.GetValue<double>());

        // Opacity (default 1.0): omitted
        Assert.Null(keyframesJson["opacity"]);

        // Volume (constant +0.5 dB): preserved as a dB keyframe; static volume remains omitted.
        var volumeJson = keyframesJson["volume"]?.AsArray();
        Assert.NotNull(volumeJson);
        var volumeKf = volumeJson[0]?.AsArray();
        Assert.NotNull(volumeKf);
        Assert.Equal(0, volumeKf[0]?.GetValue<int>());
        Assert.Equal(0.5, volumeKf[1]?.GetValue<double>());
        Assert.Null(compVideoClip["volume"]);

        // Verify Color Grade Conversion
        var colorJson = compVideoClip["color"];
        Assert.NotNull(colorJson);
        Assert.Equal(1.5, colorJson["exposure"]?.GetValue<double>());

        // Verify Caption Folding: cap1, cap2, cap3 folded into a group
        var textTrackJson = tracksJson.FirstOrDefault(t => t?["type"]?.GetValue<string>() == "text");
        Assert.NotNull(textTrackJson);
        Assert.Null(textTrackJson["clips"]);
        var captionGroupsJson = textTrackJson["captionGroups"]?.AsArray();
        Assert.NotNull(captionGroupsJson);
        Assert.Single(captionGroupsJson);
        var capGroup = Assert.IsType<JsonObject>(captionGroupsJson[0]);
        Assert.Equal("sub_group_1", capGroup["captionGroupId"]?.GetValue<string>());
        Assert.Equal(3, capGroup["clipCount"]?.GetValue<int>());
        Assert.Contains("Hello … Test", capGroup["textPreview"]?.GetValue<string>());

        // 8. Test list_models wiring with FakeGenerationClient
        ToolExecutor.GenerationServiceOverride = null;
        ToolExecutor.ResetGenerationService();
        Environment.SetEnvironmentVariable("TEMNO_USE_FAKE_GENERATION", "true");
        var listModelsRes = ToolExecutor.Execute("list_models", new JsonObject { ["type"] = "video" });
        Assert.False(listModelsRes.IsError);
        var modelsJson = JsonNode.Parse(listModelsRes.Content[0].Text!);
        Assert.NotNull(modelsJson);
        var modelsList = modelsJson["models"]?.AsArray();
        Assert.NotNull(modelsList);
        Assert.NotEmpty(modelsList);
        Assert.Contains(modelsList, m => m?["id"]?.GetValue<string>() == "video-gen-v1");
        Assert.True(modelsJson["demo"]?.GetValue<bool>());
        Assert.Equal(ToolExecutor.FakeGenerationNote, modelsJson["note"]?.GetValue<string>());

        // Clean up environment variables
        Environment.SetEnvironmentVariable("TEMNO_USE_FAKE_GENERATION", null);
        ToolExecutor.ResetGenerationService();
    }

    [Fact]
    public void ListModels_WhenUnconfigured_ReturnsConfiguredFalse()
    {
        var prevFake = Environment.GetEnvironmentVariable("TEMNO_USE_FAKE_GENERATION");
        var prevKey = Environment.GetEnvironmentVariable("TEMNO_API_KEY");
        var prevUrl = Environment.GetEnvironmentVariable("TEMNO_API_BASE_URL");
        var prevOverride = ToolExecutor.GenerationServiceOverride;
        var prefsFile = Path.Combine(Path.GetTempPath(), "temno-listmodels-prefs-" + Guid.NewGuid().ToString("N") + ".json");
        UserPreferences.SetPathForTests(prefsFile);
        try
        {
            ToolExecutor.GenerationServiceOverride = null;
            Environment.SetEnvironmentVariable("TEMNO_USE_FAKE_GENERATION", null);
            Environment.SetEnvironmentVariable("TEMNO_API_KEY", null);
            Environment.SetEnvironmentVariable("TEMNO_API_BASE_URL", null);
            ToolExecutor.ResetGenerationService();

            var listModelsRes = ToolExecutor.Execute("list_models", new JsonObject { ["type"] = "video" });
            Assert.False(listModelsRes.IsError);
            var modelsJson = JsonNode.Parse(listModelsRes.Content[0].Text!);
            Assert.NotNull(modelsJson);
            Assert.False(modelsJson["configured"]?.GetValue<bool>());
            Assert.False(modelsJson["loaded"]?.GetValue<bool>());
            var modelsList = modelsJson["models"]?.AsArray();
            Assert.NotNull(modelsList);
            Assert.Empty(modelsList);
            Assert.Contains("TEMNO_API_KEY", modelsJson["note"]?.GetValue<string>());
            Assert.Contains("TEMNO_USE_FAKE_GENERATION", modelsJson["note"]?.GetValue<string>());
        }
        finally
        {
            Environment.SetEnvironmentVariable("TEMNO_USE_FAKE_GENERATION", prevFake);
            Environment.SetEnvironmentVariable("TEMNO_API_KEY", prevKey);
            Environment.SetEnvironmentVariable("TEMNO_API_BASE_URL", prevUrl);
            ToolExecutor.GenerationServiceOverride = prevOverride;
            ToolExecutor.ResetGenerationService();
            UserPreferences.SetPathForTests(null);
            try { File.Delete(prefsFile); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void ImportMedia_InlineBytesAndMatte_ArePackageOwnedAndRoundTrip()
    {
        var created = ToolExecutor.Execute("new_project", new JsonObject { ["name"] = "InlineImportProject" });
        Assert.False(created.IsError);
        var session = Assert.IsType<ProjectSession>(ProjectHub.ActiveSession);

        var bytes = ToolExecutor.Execute("import_media", new JsonObject
        {
            ["source"] = new JsonObject
            {
                ["bytes"] = Convert.ToBase64String(ValidPngBytes),
                ["mimeType"] = "image/png",
            },
            ["name"] = "Inline bytes",
            ["folder"] = "B-roll/Inline",
        });
        Assert.False(bytes.IsError);
        var bytesEntry = JsonNode.Parse(bytes.Content[0].Text!);
        Assert.NotNull(bytesEntry);
        var bytesId = bytesEntry["id"]!.GetValue<string>();

        var matte = ToolExecutor.Execute("import_media", new JsonObject
        {
            ["source"] = new JsonObject
            {
                ["matte"] = new JsonObject { ["hex"] = "#1464DC", ["aspectRatio"] = "1:1" },
            },
            ["folder"] = "B-roll/Inline",
        });
        Assert.False(matte.IsError);
        var matteEntry = JsonNode.Parse(matte.Content[0].Text!);
        Assert.NotNull(matteEntry);
        var matteId = matteEntry["id"]!.GetValue<string>();

        session.Save();
        var reopened = ProjectSession.Open(session.PackagePath!);
        var entries = reopened.Manifest.Entries.Where(entry =>
            IdMatches(bytesId, entry.Id) || IdMatches(matteId, entry.Id)).ToList();
        Assert.Equal(2, entries.Count);
        Assert.All(entries, entry =>
        {
            Assert.NotNull(entry.FolderId);
            var path = reopened.Resolver.ResolvePath(entry);
            Assert.NotNull(path);
            Assert.True(File.Exists(path));
            Assert.StartsWith(Path.Combine(session.PackagePath!, "media"), path!, StringComparison.OrdinalIgnoreCase);
        });
        var persistedMatte = Assert.Single(entries, entry => IdMatches(matteId, entry.Id));
        Assert.Equal(1080, persistedMatte.SourceWidth);
        Assert.Equal(1080, persistedMatte.SourceHeight);
    }

    [Fact]
    public void ImportMedia_PathImportsAreUniqueValidatedAndReopenable()
    {
        var created = ToolExecutor.Execute("new_project", new JsonObject { ["name"] = "PathImportProject" });
        Assert.False(created.IsError);
        var session = Assert.IsType<ProjectSession>(ProjectHub.ActiveSession);
        var firstDirectory = Path.Combine(_tempDir, "first");
        var secondDirectory = Path.Combine(_tempDir, "second");
        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);
        var firstSource = Path.Combine(firstDirectory, "camera.png");
        var secondSource = Path.Combine(secondDirectory, "camera.png");
        File.WriteAllBytes(firstSource, ValidPngBytes);
        File.WriteAllBytes(secondSource, [.. ValidPngBytes, 0x01]);

        var first = ToolExecutor.Execute("import_media", new JsonObject { ["path"] = firstSource });
        var second = ToolExecutor.Execute("import_media", new JsonObject
        {
            ["source"] = new JsonObject { ["path"] = secondSource },
        });

        Assert.False(first.IsError);
        Assert.False(second.IsError);
        Assert.Equal(2, session.Manifest.Entries.Count);
        var firstPath = session.Resolver.ResolvePath(session.Manifest.Entries[0]);
        var secondPath = session.Resolver.ResolvePath(session.Manifest.Entries[1]);
        Assert.NotEqual(firstPath, secondPath);
        Assert.Equal(ValidPngBytes, File.ReadAllBytes(firstPath!));
        Assert.Equal([.. ValidPngBytes, 0x01], File.ReadAllBytes(secondPath!));

        var packageOwnedReimport = ToolExecutor.Execute("import_media", new JsonObject { ["path"] = firstPath });
        Assert.False(packageOwnedReimport.IsError);
        Assert.Equal(3, session.Manifest.Entries.Count);
        Assert.Equal(ValidPngBytes, File.ReadAllBytes(session.Resolver.ResolvePath(session.Manifest.Entries[2])!));

        var unsupportedPath = Path.Combine(_tempDir, "notes.txt");
        File.WriteAllText(unsupportedPath, "not media");
        var unsupported = ToolExecutor.Execute("import_media", new JsonObject { ["path"] = unsupportedPath });
        Assert.True(unsupported.IsError);
        Assert.Contains("unsupported file extension", unsupported.Content[0].Text);
        Assert.Equal(3, session.Manifest.Entries.Count);

        var ambiguous = ToolExecutor.Execute("import_media", new JsonObject
        {
            ["path"] = firstSource,
            ["source"] = new JsonObject { ["path"] = secondSource },
        });
        Assert.True(ambiguous.IsError);
        Assert.Contains("either legacy path or source", ambiguous.Content[0].Text);

        session.Save();
        var reopened = ProjectSession.Open(session.PackagePath!);
        Assert.Equal(3, reopened.Manifest.Entries.Count);
        Assert.Equal(ValidPngBytes, File.ReadAllBytes(reopened.Resolver.ResolvePath(reopened.Manifest.Entries[0])!));
        Assert.Equal([.. ValidPngBytes, 0x01], File.ReadAllBytes(reopened.Resolver.ResolvePath(reopened.Manifest.Entries[1])!));
        Assert.Equal(ValidPngBytes, File.ReadAllBytes(reopened.Resolver.ResolvePath(reopened.Manifest.Entries[2])!));
    }

    [Fact]
    public void ImportMediaFile_UsesTheExplicitSessionInsteadOfTheGlobalActiveSession()
    {
        var firstPackage = Path.Combine(_tempDir, "first.palmier");
        var secondPackage = Path.Combine(_tempDir, "second.palmier");
        var firstSession = ProjectSession.CreateNew();
        var secondSession = ProjectSession.CreateNew();
        firstSession.Save(firstPackage);
        secondSession.Save(secondPackage);
        ProjectHub.ActiveSession = secondSession;
        var source = Path.Combine(_tempDir, "explicit.png");
        File.WriteAllBytes(source, ValidPngBytes);

        var result = ToolExecutor.ImportMediaFile(firstSession, source);

        Assert.False(result.IsError);
        Assert.Single(firstSession.Manifest.Entries);
        Assert.Empty(secondSession.Manifest.Entries);
        Assert.Equal(ValidPngBytes, File.ReadAllBytes(firstSession.Resolver.ResolvePath(firstSession.Manifest.Entries[0])!));
    }

    [Fact]
    public void ImportMedia_CorruptSupportedFileIsRejectedWithoutPackageMutation()
    {
        var created = ToolExecutor.Execute("new_project", new JsonObject { ["name"] = "CorruptImportProject" });
        Assert.False(created.IsError);
        var session = Assert.IsType<ProjectSession>(ProjectHub.ActiveSession);
        var source = Path.Combine(_tempDir, "corrupt.mp4");
        File.WriteAllText(source, "not a media container");

        var result = ToolExecutor.Execute("import_media", new JsonObject { ["path"] = source });

        Assert.True(result.IsError);
        Assert.Contains("media validation failed", result.Content[0].Text);
        Assert.Empty(session.Manifest.Entries);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(session.PackagePath!, "media")));
    }

    [Fact]
    public void ImportMedia_UrlRequiresHttpsAndRejectsEmbeddedCredentialsBeforeNetworkAccess()
    {
        var created = ToolExecutor.Execute("new_project", new JsonObject { ["name"] = "UrlImportProject" });
        Assert.False(created.IsError);

        var insecure = ToolExecutor.Execute("import_media", new JsonObject
        {
            ["source"] = new JsonObject { ["url"] = "http://127.0.0.1/media.mp4" },
        });
        var credentials = ToolExecutor.Execute("import_media", new JsonObject
        {
            ["source"] = new JsonObject { ["url"] = "https://user:secret@example.invalid/media.mp4" },
        });

        Assert.True(insecure.IsError);
        Assert.Contains("HTTPS", insecure.Content[0].Text);
        Assert.True(credentials.IsError);
        Assert.Contains("must not embed credentials", credentials.Content[0].Text);
        Assert.Empty(Assert.IsType<ProjectSession>(ProjectHub.ActiveSession).Manifest.Entries);
    }

    internal static bool IdMatches(string? returned, string full) =>
        !string.IsNullOrEmpty(returned)
        && (string.Equals(returned, full, StringComparison.OrdinalIgnoreCase)
            || (returned.Length >= ToolIdShortener.PrefixFloor
                && full.StartsWith(returned, StringComparison.OrdinalIgnoreCase)));
}

