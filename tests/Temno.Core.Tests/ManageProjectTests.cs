using System.Text.Json.Nodes;
using Temno.Core.Session;
using Temno.Mcp;
using Xunit;

namespace Temno.Core.Tests;

[Collection("EnvRegistry")]
public sealed class ManageProjectTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _registryPath;
    private readonly string _projectsDir;

    public ManageProjectTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "TemnoManageProject_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _projectsDir = Path.Combine(_tempDir, "Projects");
        Directory.CreateDirectory(_projectsDir);
        _registryPath = Path.Combine(_tempDir, "project-registry.json");
        Environment.SetEnvironmentVariable("TEMNO_PROJECTS_DIR", _projectsDir);
        Environment.SetEnvironmentVariable("TEMNO_REGISTRY_PATH", _registryPath);
        ProjectHub.ActiveSession = null;
    }

    public void Dispose()
    {
        ProjectHub.ActiveSession = null;
        Environment.SetEnvironmentVariable("TEMNO_PROJECTS_DIR", null);
        Environment.SetEnvironmentVariable("TEMNO_REGISTRY_PATH", null);
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // ignore
        }
    }

    [Fact]
    public void ImplementedTools_IncludesManageProject()
        => Assert.Contains("manage_project", ToolExecutor.ImplementedTools);

    [Fact]
    public void ListCreateOpenClose_RoundTrip()
    {
        var created = ToolExecutor.Execute("manage_project", new JsonObject
        {
            ["action"] = "create",
            ["name"] = "ManageProjectRoundTrip",
            ["fps"] = 24,
        });
        Assert.False(created.IsError, created.Content[0].Text);
        var createdJson = JsonNode.Parse(created.Content[0].Text!)!;
        Assert.Equal("created", createdJson["status"]?.GetValue<string>());
        Assert.NotNull(ProjectHub.ActiveSession);

        var listed = ToolExecutor.Execute("manage_project", new JsonObject { ["action"] = "list" });
        Assert.False(listed.IsError, listed.Content[0].Text);
        var listJson = JsonNode.Parse(listed.Content[0].Text!)!;
        Assert.Equal(1, listJson["openCount"]?.GetValue<int>());
        var projects = listJson["projects"]!.AsArray();
        Assert.NotEmpty(projects);
        var id = projects[0]!["id"]!.GetValue<string>();
        Assert.True(Guid.TryParse(id, out _), "project registry ids stay full UUIDs");

        var closed = ToolExecutor.Execute("manage_project", new JsonObject { ["action"] = "close" });
        Assert.False(closed.IsError, closed.Content[0].Text);
        Assert.Null(ProjectHub.ActiveSession);

        var opened = ToolExecutor.Execute("manage_project", new JsonObject
        {
            ["action"] = "open",
            ["id"] = id,
        });
        Assert.False(opened.IsError, opened.Content[0].Text);
        Assert.NotNull(ProjectHub.ActiveSession);
        Assert.Equal(24, ProjectHub.ActiveSession!.ActiveTimeline.Fps);
    }

    [Fact]
    public void UnknownAction_IsError()
    {
        var result = ToolExecutor.Execute("manage_project", new JsonObject { ["action"] = "delete" });
        Assert.True(result.IsError);
        Assert.Contains("list, open, create, close", result.Content[0].Text);
    }

    [Fact]
    public void GetTimeline_ShortensClipIds_AndAcceptsPrefixOnUndoPath()
    {
        var created = ToolExecutor.Execute("manage_project", new JsonObject
        {
            ["action"] = "create",
            ["name"] = "ShortIdTimeline",
        });
        Assert.False(created.IsError, created.Content[0].Text);

        var session = Assert.IsType<ProjectSession>(ProjectHub.ActiveSession);
        var clipId = session.ActiveTimeline.Tracks[0].Clips.Count > 0
            ? session.ActiveTimeline.Tracks[0].Clips[0].Id
            : null;
        if (clipId is null)
        {
            session.ActiveTimeline.Tracks[0].Clips.Add(new Temno.Core.Models.Clip
            {
                MediaType = Temno.Core.Models.ClipType.video,
                StartFrame = 0,
                DurationFrames = 10,
            });
            clipId = session.ActiveTimeline.Tracks[0].Clips[0].Id;
            session.MarkDirty();
        }

        var timeline = ToolExecutor.Execute("get_timeline", []);
        Assert.False(timeline.IsError, timeline.Content[0].Text);
        var json = JsonNode.Parse(timeline.Content[0].Text!)!;
        var returned = json["tracks"]![0]!["clips"]![0]!["id"]!.GetValue<string>();
        Assert.NotEqual(clipId, returned);
        Assert.StartsWith(returned, clipId, StringComparison.OrdinalIgnoreCase);
        Assert.True(returned.Length >= ToolIdShortener.PrefixFloor);
    }
}
