using System.Text.Json.Nodes;
using Temno.Core.Models;
using Temno.Core.Session;
using Temno.Mcp;
using Xunit;

namespace Temno.Core.Tests;

// WP-337: arguments that carry an id take the 8-hex prefixes MCP hands out. add_transition refused them before,
// because clipIdA and clipIdB were not expanded (WP-320's harness hit it).
[Collection("EnvRegistry")]
public sealed class ShortIdArgumentTests : IDisposable
{
    private const string A = "1A2B3C4D-0000-4000-8000-00000000000A";
    private const string B = "9F8E7D6C-0000-4000-8000-00000000000B";

    public void Dispose() => ProjectHub.ActiveSession = null;

    [Fact]
    public void Add_transition_takes_the_ids_get_timeline_hands_out()
    {
        var session = ProjectSession.CreateNew(fps: 30);
        Assert.True(EditCommands.AddClips(session,
        [
            (0, new Clip { Id = A, MediaRef = "5E5E5E5E-0000-4000-8000-0000000000A1", MediaType = ClipType.video, StartFrame = 0, DurationFrames = 60 }),
            (0, new Clip { Id = B, MediaRef = "6F6F6F6F-0000-4000-8000-0000000000B1", MediaType = ClipType.video, StartFrame = 60, DurationFrames = 60 }),
        ]).Ok);
        ProjectHub.ActiveSession = session;

        var timeline = ToolExecutor.Execute("get_timeline", new JsonObject());
        Assert.False(timeline.IsError, timeline.Content[0].Text);
        var shortA = A[..8];
        var shortB = B[..8];
        Assert.Contains($"\"{shortA}\"", timeline.Content[0].Text);
        Assert.Contains($"\"{shortB}\"", timeline.Content[0].Text);
        Assert.DoesNotContain(A, timeline.Content[0].Text);

        var result = ToolExecutor.Execute("add_transition", new JsonObject
        {
            ["clipIdA"] = shortA,
            ["clipIdB"] = shortB,
            ["durationFrames"] = 10,
        });
        Assert.False(result.IsError, result.Content[0].Text);
        var transition = Assert.Single(session.ActiveTimeline.Transitions);
        Assert.Equal(A, transition.ClipIdA);
        Assert.Equal(B, transition.ClipIdB);
    }

    [Fact]
    public void The_mask_subtitle_and_multicam_master_keys_expand_and_curve_points_are_left_alone()
    {
        string[] full = [A, B, "3C3C3C3C-0000-4000-8000-00000000000C", "4D4D4D4D-0000-4000-8000-00000000000D", "7E7E7E7E-0000-4000-8000-00000000000E"];
        var universe = new HashSet<string>(full, StringComparer.OrdinalIgnoreCase);
        var args = JsonNode.Parse($$"""
            {
              "clipIdA": "{{full[0][..8]}}",
              "clipIdB": "{{full[1][..8]}}",
              "maskMediaRef": "{{full[2][..8]}}",
              "subtitleMediaRef": "{{full[3][..8]}}",
              "create": { "master": "{{full[4][..8]}}", "members": [{ "mediaRef": "{{full[4][..8]}}", "kind": "angle" }] },
              "curves": { "master": [[0, 0], [1, 1]] }
            }
            """)!.AsObject();

        var expanded = ToolIdShortener.Expand(args, universe);
        Assert.Equal(full[0], expanded["clipIdA"]!.GetValue<string>());
        Assert.Equal(full[1], expanded["clipIdB"]!.GetValue<string>());
        Assert.Equal(full[2], expanded["maskMediaRef"]!.GetValue<string>());
        Assert.Equal(full[3], expanded["subtitleMediaRef"]!.GetValue<string>());
        Assert.Equal(full[4], expanded["create"]!["master"]!.GetValue<string>());
        Assert.Equal(full[4], expanded["create"]!["members"]![0]!["mediaRef"]!.GetValue<string>());
        Assert.Equal("[[0,0],[1,1]]", expanded["curves"]!["master"]!.ToJsonString());
    }
}
