using System.Text.Json.Nodes;
using Temno.Core.Captions;
using Temno.Core.Models;
using Temno.Core.Session;
using Temno.Core.Transcription;
using Temno.Mcp;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// WP-115: the Mac add_captions / get_transcript arguments Windows was missing —
/// maxCharacters, maximumGapSeconds, trackIndex, subtitleMediaRef, transform.rotation.
/// </summary>
[Collection("EnvRegistry")]
public sealed class CaptionMcpContractTests : IDisposable
{
    private readonly string _dir;
    private readonly string _media;

    public CaptionMcpContractTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "temno-caption-mcp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        TranscriptDiskCache.SetRootForTests(Path.Combine(_dir, "cache"));
        _media = Path.Combine(_dir, "a.mp4");
        File.WriteAllText(_media, "a");
    }

    public void Dispose()
    {
        ToolExecutor.TranscriptionProviderOverride = null;
        ProjectHub.ActiveSession = null;
        TranscriptDiskCache.SetRootForTests(null);
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private ProjectSession Session()
    {
        var session = ProjectSession.CreateNew(fps: 30, width: 1920, height: 1080);
        session.Manifest.Entries.Add(new MediaManifestEntry
        {
            Id = "M1", Name = "a", Type = ClipType.video, HasAudio = true,
            Source = MediaSource.External(_media), Duration = 10,
        });
        session.ActiveTimeline.Tracks[0].Clips.Add(new Clip
        {
            Id = "src", MediaRef = "M1", MediaType = ClipType.video, StartFrame = 0, DurationFrames = 120,
        });
        ProjectHub.ActiveSession = session;
        ToolExecutor.TranscriptionProviderOverride = new FakeTranscriptionProvider(
            FakeTranscriptionProvider.MakeTranscript(["hello", "brave", "new", "world"]));
        return session;
    }

    private static JsonObject AddCaptionsSchema()
    {
        var schema = McpToolRegistry.Tools.Single(t => t.Name == "add_captions").InputSchema;
        return (JsonObject)schema["properties"]!;
    }

    [Fact]
    public void SchemaAdvertisesTheMacArguments()
    {
        var props = AddCaptionsSchema();
        Assert.NotNull(props["subtitleMediaRef"]);
        Assert.NotNull(props["maxCharacters"]);
        Assert.NotNull(props["maximumGapSeconds"]);
        Assert.NotNull(props["trackIndex"]);

        var transform = (JsonObject)((JsonObject)props["transform"]!)["properties"]!;
        Assert.NotNull(transform["rotation"]);

        var transcript = (JsonObject)McpToolRegistry.Tools
            .Single(t => t.Name == "get_transcript").InputSchema["properties"]!;
        Assert.NotNull(transcript["trackIndex"]);
    }

    /// <summary>
    /// add_captions now rejects unknown keys against its registered schema, so every argument the
    /// executor honours must be declared or previously valid calls start failing.
    /// </summary>
    [Fact]
    public void SchemaDeclaresEveryArgumentTheExecutorHonours()
    {
        string[] honoured =
        [
            "language", "transform", "textCase", "censorProfanity",
            "maxWords", "maxCharacters", "maximumGapSeconds", "trackIndex", "subtitleMediaRef",
            "animation", "highlightColor",
            // TextStylePatch keys read by ParseTextStylePatch.
            "fontName", "fontSize", "isBold", "isItalic",
            "color", "alignment", "borderColor", "backgroundColor",
        ];

        var props = AddCaptionsSchema();
        foreach (var key in honoured)
            Assert.True(props[key] is not null, $"add_captions schema is missing '{key}'");
    }

    [Fact]
    public void AFullStylePatchStillPassesValidation()
    {
        var session = Session();
        var result = ToolExecutor.Execute("add_captions", new JsonObject
        {
            ["fontName"] = "Segoe UI",
            ["fontSize"] = 52,
            ["isBold"] = true,
            ["isItalic"] = false,
            ["color"] = "#FFFFFF",
            ["alignment"] = "center",
            ["borderColor"] = "#000000",
            ["backgroundColor"] = "#101010",
            ["animation"] = "highlightPop",
            ["highlightColor"] = "#FFD400",
            ["textCase"] = "upper",
            ["maxWords"] = 4,
            ["language"] = "en",
        });

        Assert.False(result.IsError, result.Content[0].Text);
        Assert.NotEmpty(session.ActiveTimeline.Tracks[0].Clips);
    }

    [Theory]
    [InlineData(3.0)]
    [InlineData(-0.5)]
    public void MaximumGapSecondsOutsideTheMacRangeIsRejected(double gap)
    {
        Session();
        var result = ToolExecutor.Execute("add_captions", new JsonObject { ["maximumGapSeconds"] = gap });

        Assert.True(result.IsError);
        Assert.Contains("maximumGapSeconds", result.Content[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void MaximumGapSecondsInsideTheRangeIsAccepted()
    {
        var session = Session();
        var result = ToolExecutor.Execute("add_captions", new JsonObject
        {
            ["maximumGapSeconds"] = CaptionSpecBuilder.MaximumGapSecondsLimit,
        });

        Assert.False(result.IsError, result.Content[0].Text);
        Assert.NotEmpty(session.ActiveTimeline.Tracks[0].Clips);
    }

    [Fact]
    public void MaxCharactersMustBeAtLeastOne()
    {
        Session();
        var result = ToolExecutor.Execute("add_captions", new JsonObject { ["maxCharacters"] = 0 });

        Assert.True(result.IsError);
        Assert.Contains("maxCharacters", result.Content[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void MaxCharactersSplitsGeneratedCaptions()
    {
        var session = Session();
        var result = ToolExecutor.Execute("add_captions", new JsonObject { ["maxCharacters"] = 12 });

        Assert.False(result.IsError, result.Content[0].Text);
        var texts = session.ActiveTimeline.Tracks[0].Clips.Select(c => c.TextContent!).ToList();
        Assert.True(texts.Count > 1);
        Assert.All(texts, t => Assert.True(t.Length <= 12 || !t.Contains(' '), t));
    }

    [Fact]
    public void TrackIndexOutOfRangeIsRejected()
    {
        Session();
        var result = ToolExecutor.Execute("add_captions", new JsonObject { ["trackIndex"] = 99 });

        Assert.True(result.IsError);
        Assert.Contains("trackIndex", result.Content[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void TrackIndexSelectsTheCaptionedTrack()
    {
        var session = Session();
        var result = ToolExecutor.Execute("add_captions", new JsonObject { ["trackIndex"] = 0 });

        Assert.False(result.IsError, result.Content[0].Text);
        Assert.NotEmpty(session.ActiveTimeline.Tracks[0].Clips);
        Assert.All(session.ActiveTimeline.Tracks[0].Clips, c => Assert.Equal(ClipType.text, c.MediaType));
    }

    [Fact]
    public void TransformRotationReachesTheGeneratedCaptions()
    {
        var session = Session();
        var result = ToolExecutor.Execute("add_captions", new JsonObject
        {
            ["transform"] = new JsonObject
            {
                ["centerX"] = 0.5,
                ["centerY"] = 0.85,
                ["rotation"] = 15,
            },
        });

        Assert.False(result.IsError, result.Content[0].Text);
        var captions = session.ActiveTimeline.Tracks[0].Clips;
        Assert.NotEmpty(captions);
        Assert.All(captions, c =>
        {
            Assert.NotNull(c.Transform);
            Assert.Equal(15, c.Transform!.Rotation);
            Assert.Equal(0.85, c.Transform.CenterY, 3);
        });
    }

    [Fact]
    public void CensorProfanityStaysRejected()
    {
        Session();
        var result = ToolExecutor.Execute("add_captions", new JsonObject { ["censorProfanity"] = true });

        Assert.True(result.IsError);
        Assert.Contains("censorProfanity", result.Content[0].Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Whisper", result.Content[0].Text, StringComparison.OrdinalIgnoreCase);
    }
}
