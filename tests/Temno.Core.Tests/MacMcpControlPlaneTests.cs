using System.Text.Json.Nodes;
using Temno.Core.Models;
using Temno.Core.Session;
using Temno.Mcp;
using Xunit;

namespace Temno.Core.Tests;

[Collection("EnvRegistry")]
public sealed class MacMcpControlPlaneTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _registryPath;
    private readonly string _projectsDir;
    private readonly string _downloadsDir;

    public MacMcpControlPlaneTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "TemnoMacMcp_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _projectsDir = Path.Combine(_tempDir, "Projects");
        _downloadsDir = Path.Combine(_tempDir, "Downloads");
        Directory.CreateDirectory(_projectsDir);
        Directory.CreateDirectory(_downloadsDir);
        _registryPath = Path.Combine(_tempDir, "project-registry.json");
        Environment.SetEnvironmentVariable("TEMNO_PROJECTS_DIR", _projectsDir);
        Environment.SetEnvironmentVariable("TEMNO_REGISTRY_PATH", _registryPath);
        Environment.SetEnvironmentVariable("TEMNO_DOWNLOADS_DIR", _downloadsDir);
        ProjectHub.ActiveSession = null;
    }

    public void Dispose()
    {
        ProjectHub.ActiveSession = null;
        Environment.SetEnvironmentVariable("TEMNO_PROJECTS_DIR", null);
        Environment.SetEnvironmentVariable("TEMNO_REGISTRY_PATH", null);
        Environment.SetEnvironmentVariable("TEMNO_DOWNLOADS_DIR", null);
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch { /* ignore */ }
    }

    [Fact]
    public void GetTimeline_EmitsTrackId()
    {
        Assert.False(ToolExecutor.Execute("new_project", new JsonObject { ["name"] = "TrackIdProject" }).IsError);
        var session = Assert.IsType<ProjectSession>(ProjectHub.ActiveSession);
        var timeline = ToolExecutor.Execute("get_timeline", []);
        Assert.False(timeline.IsError, timeline.Content[0].Text);
        var json = JsonNode.Parse(timeline.Content[0].Text!)!;
        var trackId = json["tracks"]![0]!["trackId"]!.GetValue<string>();
        Assert.StartsWith(trackId, session.ActiveTimeline.Tracks[0].Id, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AddClips_SourceSpanSetsTrim()
    {
        Assert.False(ToolExecutor.Execute("new_project", new JsonObject { ["name"] = "SourceSpanProject" }).IsError);
        var session = Assert.IsType<ProjectSession>(ProjectHub.ActiveSession);
        session.Manifest.Entries.Add(new MediaManifestEntry
        {
            Id = Timeline.NewId(),
            Name = "clip.mp4",
            Type = ClipType.video,
            Duration = 10,
        });
        var mediaId = session.Manifest.Entries[0].Id;
        var add = ToolExecutor.Execute("add_clips", new JsonObject
        {
            ["entries"] = new JsonArray
            {
                new JsonObject
                {
                    ["mediaRef"] = mediaId,
                    ["trackIndex"] = 0,
                    ["startFrame"] = 0,
                    ["source"] = new JsonArray { 1.0, 3.0 },
                },
            },
        });
        Assert.False(add.IsError, add.Content[0].Text);
        var clip = session.ActiveTimeline.Tracks[0].Clips.Single();
        Assert.Equal(30, clip.TrimStartFrame);
        Assert.Equal(60, clip.DurationFrames);
    }

    [Fact]
    public void ManageTracks_SetMuteByTrackId()
    {
        Assert.False(ToolExecutor.Execute("new_project", new JsonObject { ["name"] = "ManageTracksProject" }).IsError);
        var session = Assert.IsType<ProjectSession>(ProjectHub.ActiveSession);
        var id = session.ActiveTimeline.Tracks[0].Id;
        var result = ToolExecutor.Execute("manage_tracks", new JsonObject
        {
            ["set"] = new JsonArray
            {
                new JsonObject { ["trackId"] = id[..8], ["muted"] = true },
            },
        });
        Assert.False(result.IsError, result.Content[0].Text);
        Assert.True(session.ActiveTimeline.Tracks[0].Muted);
        var json = JsonNode.Parse(result.Content[0].Text!)!;
        Assert.NotNull(json["tracks"]);
    }

    [Fact]
    public void ExportProject_OmitsPath_WritesFcpxmlToDownloads()
    {
        Assert.False(ToolExecutor.Execute("new_project", new JsonObject { ["name"] = "ExportDefaultProject" }).IsError);
        var result = ToolExecutor.Execute("export_project", new JsonObject { ["mode"] = "fcpxml" });
        Assert.False(result.IsError, result.Content[0].Text);
        var json = JsonNode.Parse(result.Content[0].Text!)!;
        var path = json["outputPath"]!.GetValue<string>();
        Assert.StartsWith(_downloadsDir, path, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(path));
    }
}
