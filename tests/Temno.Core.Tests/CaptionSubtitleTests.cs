using System.Text;
using System.Text.Json.Nodes;
using Temno.Core.Captions;
using Temno.Core.IO;
using Temno.Core.Models;
using Temno.Core.Project;
using Temno.Core.Session;
using Temno.Mcp;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// WP-115: SRT/WebVTT assets as a first-class library type. Before this package a Mac package whose
/// media.json contained a subtitle entry made the whole manifest unreadable.
/// </summary>
[Collection("EnvRegistry")]
public sealed class CaptionSubtitleTests : IDisposable
{
    private readonly string _dir;

    public CaptionSubtitleTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "temno-subtitle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        ProjectHub.ActiveSession = null;
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private const string SampleSrt =
        "1\n00:00:00,000 --> 00:00:01,000\nFirst cue\n\n" +
        "2\n00:00:02,000 --> 00:00:03,500\nSecond cue\n\n";

    private string WriteSrt(string name = "cues.srt", string? content = null)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content ?? SampleSrt);
        return path;
    }

    // ---------------------------------------------------------------- package

    [Fact]
    public void MacManifestWithSubtitleEntry_OpensWithoutManifestUnreadable()
    {
        var package = Path.Combine(_dir, "proj.palmier");
        Directory.CreateDirectory(package);
        File.WriteAllBytes(
            Path.Combine(package, ProjectConstants.TimelineFilename),
            ProjectSession.CreateNew().ProjectFile.Encode());

        // Shaped like a Mac media.json: subtitle asset alongside a normal video asset.
        File.WriteAllText(Path.Combine(package, ProjectConstants.ManifestFilename), """
        {
          "version": 2,
          "entries": [
            {
              "id": "V1",
              "name": "shot.mov",
              "type": "video",
              "source": { "external": { "absolutePath": "/Users/x/shot.mov" } },
              "duration": 12.5,
              "hasAudio": true
            },
            {
              "id": "S1",
              "name": "dialogue.srt",
              "type": "subtitle",
              "source": { "external": { "absolutePath": "/Users/x/dialogue.srt" } },
              "duration": 8
            }
          ],
          "folders": []
        }
        """);

        var contents = ProjectPackage.Read(package);

        Assert.False(contents.ManifestUnreadable);
        Assert.NotNull(contents.Manifest);
        Assert.Equal(2, contents.Manifest!.Entries.Count);
        var subtitle = contents.Manifest.Entries.Single(e => e.Id == "S1");
        Assert.Equal(ClipType.subtitle, subtitle.Type);
        Assert.Equal("dialogue.srt", subtitle.Name);
    }

    [Fact]
    public void SubtitleEntryRoundTripsBackToTheMacSpelling()
    {
        var manifest = new MediaManifest();
        manifest.Entries.Add(new MediaManifestEntry
        {
            Id = "S1",
            Name = "dialogue.srt",
            Type = ClipType.subtitle,
            Source = MediaSource.External("/Users/x/dialogue.srt"),
            Duration = 8,
        });

        var json = Encoding.UTF8.GetString(manifest.Encode());
        Assert.Matches(@"""type"":\s*""subtitle""", json);
        Assert.Equal(ClipType.subtitle, MediaManifest.Decode(manifest.Encode()).Entries[0].Type);
    }

    [Fact]
    public void SubtitleAssetsAreNotTimelinePlaceable()
    {
        Assert.False(ClipType.subtitle.IsTimelinePlaceable());
        Assert.True(ClipType.video.IsTimelinePlaceable());
        Assert.True(ClipType.audio.IsTimelinePlaceable());
        Assert.True(ClipType.text.IsTimelinePlaceable());
    }

    // ---------------------------------------------------------------- import wiring

    [Fact]
    public void SubtitleExtensionsAreImportable()
    {
        Assert.Contains(".srt", MediaLibraryCommands.SupportedFileExtensions);
        Assert.Contains(".vtt", MediaLibraryCommands.SupportedFileExtensions);

        Assert.True(MediaLibraryCommands.TryGetTypeFromExtension(".srt", out var srt));
        Assert.Equal(ClipType.subtitle, srt);
        Assert.True(MediaLibraryCommands.TryGetTypeFromExtension(".vtt", out var vtt));
        Assert.Equal(ClipType.subtitle, vtt);

        Assert.True(MediaLibraryCommands.TryGetMimeType("application/x-subrip", out var ext, out var type));
        Assert.Equal("srt", ext);
        Assert.Equal(ClipType.subtitle, type);
        Assert.True(MediaLibraryCommands.TryGetMimeType("text/vtt", out var vttExt, out var vttType));
        Assert.Equal("vtt", vttExt);
        Assert.Equal(ClipType.subtitle, vttType);
    }

    // ---------------------------------------------------------------- parsing

    [Fact]
    public void ReadsCuesAndReportsDuration()
    {
        var parsed = SubtitleFile.Read(WriteSrt());

        Assert.Equal(2, parsed.Entries.Count);
        Assert.Equal("First cue", parsed.Entries[0].Text);
        Assert.Equal(3.5, SubtitleFile.DurationSeconds(parsed.Entries), 3);
    }

    [Fact]
    public void StripsInlineMarkupFromCueText()
    {
        var path = WriteSrt("styled.vtt", """
        WEBVTT

        1
        00:00:00.000 --> 00:00:01.000
        <b>Bold</b> and {\an8}positioned

        """);
        var parsed = SubtitleFile.Read(path);

        Assert.Equal("Bold and positioned", parsed.Entries[0].Text);
    }

    [Fact]
    public void FallsBackToLatin1WhenBytesAreNotUtf8()
    {
        var path = Path.Combine(_dir, "latin.srt");
        File.WriteAllBytes(path, Encoding.Latin1.GetBytes(
            "1\n00:00:00,000 --> 00:00:01,000\nCafé naïve\n\n"));

        var parsed = SubtitleFile.Read(path);
        Assert.Equal("Café naïve", parsed.Entries[0].Text);
    }

    [Fact]
    public void EmptySubtitleFileIsRejected()
    {
        var path = WriteSrt("empty.srt", "   \n\n");
        var ex = Assert.Throws<InvalidDataException>(() => SubtitleFile.Read(path));
        Assert.Contains("no cues", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AssSubtitlesAreRejectedLikeMac()
    {
        var path = Path.Combine(_dir, "styled.ass");
        File.WriteAllText(path, "[Script Info]\n");
        var ex = Assert.Throws<InvalidDataException>(() => SubtitleFile.Read(path));
        Assert.Contains("Use SRT or WebVTT", ex.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- add_captions subtitleMediaRef

    private ProjectSession SessionWithSubtitleAsset(string? srtPath = null)
    {
        var session = ProjectSession.CreateNew(fps: 30, width: 1920, height: 1080);
        session.Manifest.Entries.Add(new MediaManifestEntry
        {
            Id = "S1",
            Name = "dialogue.srt",
            Type = ClipType.subtitle,
            Source = MediaSource.External(srtPath ?? WriteSrt()),
            Duration = 3.5,
        });
        ProjectHub.ActiveSession = session;
        return session;
    }

    [Fact]
    public void AddCaptionsFromSubtitleAsset_PlacesCuesOnANewTopTrack()
    {
        var session = SessionWithSubtitleAsset();
        var tracksBefore = session.ActiveTimeline.Tracks.Count;

        var result = ToolExecutor.Execute("add_captions", new JsonObject { ["subtitleMediaRef"] = "S1" });

        Assert.False(result.IsError, result.Content[0].Text);
        Assert.Equal(tracksBefore + 1, session.ActiveTimeline.Tracks.Count);

        var captions = session.ActiveTimeline.Tracks[0].Clips;
        Assert.Equal(2, captions.Count);
        Assert.All(captions, c => Assert.Equal(ClipType.text, c.MediaType));
        Assert.Equal("First cue", captions[0].TextContent);
        Assert.Equal("Second cue", captions[1].TextContent);
        // Gap closing is off for subtitle import, so cues keep their authored frames.
        Assert.Equal(0, captions[0].StartFrame);
        Assert.Equal(30, captions[0].DurationFrames);
        Assert.Equal(60, captions[1].StartFrame);
        Assert.Equal(45, captions[1].DurationFrames);
        // One caption group, one undo step.
        Assert.NotNull(captions[0].CaptionGroupId);
        Assert.Equal(captions[0].CaptionGroupId, captions[1].CaptionGroupId);

        session.Undo();
        Assert.Equal(tracksBefore, session.ActiveTimeline.Tracks.Count);
    }

    [Fact]
    public void SubtitleMediaRefCannotBeCombinedWithOtherArguments()
    {
        SessionWithSubtitleAsset();

        var result = ToolExecutor.Execute("add_captions", new JsonObject
        {
            ["subtitleMediaRef"] = "S1",
            ["maxWords"] = 4,
        });

        Assert.True(result.IsError);
        Assert.Contains("subtitleMediaRef", result.Content[0].Text, StringComparison.Ordinal);
        Assert.Contains("maxWords", result.Content[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptySubtitleAssetIsReportedAsAnError()
    {
        SessionWithSubtitleAsset(WriteSrt("blank.srt", "\n\n"));

        var result = ToolExecutor.Execute("add_captions", new JsonObject { ["subtitleMediaRef"] = "S1" });

        Assert.True(result.IsError);
        Assert.Contains("no cues", result.Content[0].Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SubtitleMediaRefMustPointAtASubtitleAsset()
    {
        var session = SessionWithSubtitleAsset();
        session.Manifest.Entries.Add(new MediaManifestEntry
        {
            Id = "V1", Name = "shot.mov", Type = ClipType.video,
            Source = MediaSource.External(Path.Combine(_dir, "shot.mov")), Duration = 3,
        });

        var result = ToolExecutor.Execute("add_captions", new JsonObject { ["subtitleMediaRef"] = "V1" });

        Assert.True(result.IsError);
        Assert.Contains("SRT or WebVTT", result.Content[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void SubtitleAssetsCannotBePlacedOnTheTimeline()
    {
        var session = SessionWithSubtitleAsset();
        var tracksBefore = session.ActiveTimeline.Tracks.Count;

        var added = ToolExecutor.Execute("add_clips", new JsonObject
        {
            ["entries"] = new JsonArray
            {
                new JsonObject { ["mediaRef"] = "S1", ["startFrame"] = 0 },
            },
        });

        Assert.True(added.IsError);
        Assert.Contains("cannot be placed on the timeline", added.Content[0].Text, StringComparison.Ordinal);
        Assert.Contains("subtitleMediaRef", added.Content[0].Text, StringComparison.Ordinal);
        Assert.Equal(tracksBefore, session.ActiveTimeline.Tracks.Count);
        Assert.Empty(session.ActiveTimeline.Tracks.SelectMany(t => t.Clips));

        var inserted = ToolExecutor.Execute("insert_clips", new JsonObject
        {
            ["trackIndex"] = 0,
            ["atFrame"] = 0,
            ["entries"] = new JsonArray
            {
                new JsonObject { ["mediaRef"] = "S1" },
            },
        });

        Assert.True(inserted.IsError);
        Assert.Contains("cannot be placed on the timeline", inserted.Content[0].Text, StringComparison.Ordinal);
        Assert.Empty(session.ActiveTimeline.Tracks.SelectMany(t => t.Clips));
    }

    [Fact]
    public void UnknownAddCaptionsArgumentsAreRejected()
    {
        SessionWithSubtitleAsset();

        var result = ToolExecutor.Execute("add_captions", new JsonObject { ["notAThing"] = 1 });

        Assert.True(result.IsError);
        Assert.Contains("notAThing", result.Content[0].Text, StringComparison.Ordinal);
    }
}
