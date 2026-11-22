using System.Text.Json.Nodes;
using Temno.Core.IO;
using Temno.Core.Models;
using Temno.Core.Session;
using Temno.Mcp;
using Xunit;

namespace Temno.Core.Tests;

[Collection("EnvRegistry")]
public sealed class Wp96ToolSurfaceTests : IDisposable
{
    private readonly string _tempHome;

    public Wp96ToolSurfaceTests()
    {
        _tempHome = Path.Combine(Path.GetTempPath(), "temno-wp96-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempHome);
        // Isolate PresetStore/TemplateStore via env-like paths by constructing with base dir where possible.
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempHome, recursive: true); } catch { /* ignore */ }
        ProjectHub.ActiveSession = null;
    }

    [Fact]
    public void ValidateTimeline_ReportsEmpty()
    {
        var session = ProjectSession.CreateNew();
        ProjectHub.ActiveSession = session;
        var result = ToolExecutor.Execute("validate_timeline", new JsonObject());
        Assert.False(result.IsError);
        var text = result.Content[0].Text;
        Assert.Contains("warning", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SuggestCrop_CenterForWideSource()
    {
        var session = ProjectSession.CreateNew();
        ProjectHub.ActiveSession = session;
        session.Manifest.Entries.Add(new MediaManifestEntry
        {
            Id = "m1",
            Name = "wide",
            Type = ClipType.video,
            Source = MediaSource.External("/tmp/x.mp4"),
            SourceWidth = 1920,
            SourceHeight = 1080,
        });
        Assert.True(EditCommands.AddClip(session, 0, new Clip
        {
            Id = "c1",
            MediaRef = "m1",
            MediaType = ClipType.video,
            DurationFrames = 30,
        }).Ok);

        var result = ToolExecutor.Execute("suggest_crop", new JsonObject
        {
            ["clipId"] = "c1",
            ["targetAspect"] = 1.0, // square
            ["apply"] = true,
        });
        Assert.False(result.IsError);
        var clip = session.ActiveTimeline.Tracks[0].Clips[0];
        Assert.True(clip.Crop.Left > 0.01);
        Assert.True(clip.Crop.Right > 0.01);
        Assert.Equal(0, clip.Crop.Top, 5);
    }

    [Fact]
    public void TextStylePreset_RoundTrip()
    {
        var store = new PresetStore(_tempHome);
        var style = new TextStyle { FontSize = 72, IsBold = true };
        var saved = store.SaveTextStyle("Title", style);
        Assert.NotNull(store.GetTextStyle("Title"));
        Assert.NotNull(store.GetTextStyle(saved.Id));
        Assert.True(store.RemoveTextStyle("Title"));
        Assert.Null(store.GetTextStyle("Title"));
    }

    [Fact]
    public void ExportPreset_RoundTrip()
    {
        var store = new PresetStore(_tempHome);
        var p = store.SaveExportPreset("Web", VideoCodec.H264, ExportResolution.R1080p, 8, true);
        Assert.NotNull(store.GetExportPreset("Web"));
        Assert.True(store.RemoveExportPreset(p.Id));
    }

    [Fact]
    public void ProjectTemplate_SaveListRemove()
    {
        var store = new ProjectTemplateStore(_tempHome);
        var session = ProjectSession.CreateNew();
        var info = store.Save("Blank", session.ProjectFile, session.Manifest);
        Assert.Contains(store.List(), t => t.Name == "Blank");
        Assert.True(store.Remove("Blank"));
        Assert.DoesNotContain(store.List(), t => t.Name == "Blank");
        Assert.True(File.Exists(info.Path) == false || !store.List().Any());
    }

    [Fact]
    public void DenoiseAudio_StoresEffect()
    {
        var session = ProjectSession.CreateNew();
        ProjectHub.ActiveSession = session;
        Assert.True(EditCommands.AddClip(session, 2, new Clip
        {
            Id = "a1",
            MediaRef = "m",
            MediaType = ClipType.audio,
            DurationFrames = 30,
        }).Ok);

        var result = ToolExecutor.Execute("denoise_audio", new JsonObject
        {
            ["clipIds"] = new JsonArray("a1"),
            ["strength"] = 0.7,
            ["enabled"] = true,
        });
        Assert.False(result.IsError);
        var clip = session.ActiveTimeline.Tracks[2].Clips[0];
        Assert.Contains(clip.Effects ?? [], e => e.Type == Clip.DenoiseEffectType);
    }

    [Fact]
    public void DuckAudio_WritesAttackReleaseEnvelope()
    {
        var session = ProjectSession.CreateNew();
        ProjectHub.ActiveSession = session;
        // tracks: 0,1 video, 2,3 audio in CreateNew
        Assert.True(EditCommands.AddClip(session, 2, new Clip
        {
            Id = "voice", MediaRef = "v", MediaType = ClipType.audio, DurationFrames = 30, Volume = 1,
        }).Ok);
        Assert.True(EditCommands.AddClip(session, 3, new Clip
        {
            Id = "bg", MediaRef = "b", MediaType = ClipType.audio, DurationFrames = 30, Volume = 1,
        }).Ok);

        var result = ToolExecutor.Execute("duck_audio", new JsonObject
        {
            ["voiceTrack"] = 2,
            ["bgTrack"] = 3,
            ["reductionDb"] = 6,
            ["attack"] = 0.2,
            ["release"] = 0.2,
        });
        var body = string.Join("\n", result.Content.Select(c => c.Text));
        Assert.False(result.IsError, body);
        var bg = session.ActiveTimeline.Tracks[3].Clips[0];
        Assert.True(Math.Abs(bg.Volume - 1) < 0.0001,
            $"staticVolume={bg.Volume}; keyframe0={KeyframeMath.Sample(bg.VolumeTrack, 0, 0)}");
        Assert.NotNull(bg.VolumeTrack);
        Assert.InRange(bg.VolumeAt(0), 0.49, 0.51);
    }

    [Fact]
    public void DuckAudio_RejectsUnavailableTriggerSource()
    {
        var session = ProjectSession.CreateNew();
        ProjectHub.ActiveSession = session;
        Assert.True(EditCommands.AddClip(session, 2, new Clip
        {
            Id = "voice", MediaRef = "v", MediaType = ClipType.audio, DurationFrames = 30,
        }).Ok);
        Assert.True(EditCommands.AddClip(session, 3, new Clip
        {
            Id = "bg", MediaRef = "b", MediaType = ClipType.audio, DurationFrames = 30,
        }).Ok);

        var result = ToolExecutor.Execute("duck_audio", new JsonObject
        {
            ["voiceTrack"] = 2,
            ["bgTrack"] = 3,
            ["reductionDb"] = 6,
            ["source"] = "vad",
        });

        Assert.True(result.IsError);
        Assert.Contains("unavailable", result.Content[0].Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SendFeedback_WritesFile()
    {
        ProjectHub.ActiveSession = ProjectSession.CreateNew();
        var result = ToolExecutor.Execute("send_feedback", new JsonObject
        {
            ["category"] = "suggestion",
            ["summary"] = "test feedback",
            ["details"] = "wp96",
            ["severity"] = "low",
        });
        Assert.False(result.IsError);
        Assert.Contains("feedback", result.Content[0].Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ImplementedTools_IncludesWp96Surface()
    {
        Assert.Contains("validate_timeline", ToolExecutor.ImplementedTools);
        Assert.Contains("suggest_crop", ToolExecutor.ImplementedTools);
        Assert.Contains("list_text_styles", ToolExecutor.ImplementedTools);
        Assert.Contains("duck_audio", ToolExecutor.ImplementedTools);
        Assert.Contains("redo", ToolExecutor.ImplementedTools);
    }
}
