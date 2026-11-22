using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using Temno.Core;
using Temno.Core.Models;
using Temno.Core.Session;
using Temno.Mcp;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// WP-160. The owner's project holds a 25.9 GB byte-for-byte copy of a source that already existed on
/// disk, because <c>import_media</c> could only copy. <c>MediaSource.External</c> has been in the
/// on-disk format and readable by <c>MediaResolver</c> the whole time; nothing could write one.
/// </summary>
public sealed class ImportByReferenceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "temno-wp160-" + Guid.NewGuid().ToString("N"));

    private readonly string _package;
    private readonly string _source;

    public ImportByReferenceTests()
    {
        _package = Path.Combine(_root, "project.palmier");
        Directory.CreateDirectory(_package);
        _source = Path.Combine(_root, "outside-the-package.mp4");
        File.WriteAllBytes(_source, new byte[4096]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp */ }
    }

    private static MediaLibraryCommands.PreparedMediaImport Prepare(
        string package, string source, bool reference, CancellationToken ct = default)
    {
        var result = MediaLibraryCommands.PrepareImportedFile(
            package,
            manifestUnreadable: false,
            folderExists: true,
            source,
            name: null,
            folderId: null,
            prepareEntry: null,
            out var prepared,
            referenceInPlace: reference,
            cancellationToken: ct);
        Assert.True(result.Ok, result.Error);
        Assert.NotNull(prepared);
        return prepared!.Value;
    }

    [Fact]
    public void ReferenceInPlace_WritesExternalAbsolutePath_AndCopiesNothing()
    {
        var prepared = Prepare(_package, _source, reference: true);

        Assert.True(prepared.IsReference);
        Assert.Equal(_source, prepared.DestinationPath);
        var external = Assert.IsType<MediaSource.ExternalSource>(prepared.Entry.Source);
        Assert.Equal(_source, external.AbsolutePath);
        Assert.False(Directory.Exists(Path.Combine(_package, "media")));
    }

    [Fact]
    public void Copy_IsStillTheDefault_AndStillWritesIntoThePackage()
    {
        var prepared = Prepare(_package, _source, reference: false);

        Assert.False(prepared.IsReference);
        Assert.StartsWith(_package, prepared.DestinationPath, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(prepared.DestinationPath));
        Assert.IsType<MediaSource.ProjectSource>(prepared.Entry.Source);
        Assert.Equal(4096, new FileInfo(prepared.DestinationPath).Length);
    }

    [Fact]
    public void AFailedCommitNeverDeletesTheUsersOwnFile()
    {
        // The rollback in CommitImportedFile deletes DestinationPath. For a referenced import that is
        // the user's source. Without the IsReference guard this test deletes their media.
        var session = ProjectSession.CreateNew();
        var prepared = Prepare(_package, _source, reference: true);
        var doomed = prepared with { Entry = Clone(prepared.Entry, folderId: "NO-SUCH-FOLDER") };

        var commit = MediaLibraryCommands.CommitImportedFile(session, doomed);

        Assert.False(commit.Ok);
        Assert.True(File.Exists(_source), "a failed referenced import deleted the user's source file");
    }

    [Fact]
    public void AFailedCommitStillCleansUpBytesThePackageOwns()
    {
        var session = ProjectSession.CreateNew();
        var prepared = Prepare(_package, _source, reference: false);
        var doomed = prepared with { Entry = Clone(prepared.Entry, folderId: "NO-SUCH-FOLDER") };

        var commit = MediaLibraryCommands.CommitImportedFile(session, doomed);

        Assert.False(commit.Ok);
        Assert.False(File.Exists(prepared.DestinationPath));
        Assert.True(File.Exists(_source));
    }

    [Fact]
    public void ACancelledCopyLeavesNoPartialFileAndNoTempFile()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = MediaLibraryCommands.PrepareImportedFile(
            _package, false, true, _source, null, null, null,
            out var prepared, referenceInPlace: false, cancellationToken: cts.Token);

        Assert.False(result.Ok);
        Assert.Equal("import cancelled", result.Error);
        Assert.Null(prepared);
        var media = Path.Combine(_package, "media");
        if (Directory.Exists(media))
            Assert.Empty(Directory.EnumerateFiles(media, "*", SearchOption.AllDirectories));
        Assert.True(File.Exists(_source));
    }

    [Fact]
    public void AReferencedEntryRoundTripsThroughMediaJson()
    {
        var prepared = Prepare(_package, _source, reference: true);
        var manifest = new MediaManifest();
        manifest.Entries.Add(prepared.Entry);

        // The real path: MediaSourceJsonConverter is registered on JsonOptions, not by attribute, so
        // a bare JsonSerializer.Serialize would not exercise what the product writes.
        var json = JsonSerializer.Serialize(manifest, JsonOptions.ForProject);
        Assert.Contains("\"external\"", json);
        Assert.Contains("\"absolutePath\"", json);

        var read = JsonSerializer.Deserialize<MediaManifest>(json, JsonOptions.ForProject);
        var external = Assert.IsType<MediaSource.ExternalSource>(read!.Entries.Single().Source);
        Assert.Equal(_source, external.AbsolutePath);
    }

    [Fact]
    public void AReferencedSourceThatMovesGoesOffline_NotMissing()
    {
        // The read side has always handled this; the point is that the resolver reports it as offline
        // rather than throwing, so the WP-154 export preflight and the media panel's offline state
        // both see it.
        var session = ProjectSession.CreateNew();
        var prepared = Prepare(_package, _source, reference: true);
        Assert.True(MediaLibraryCommands.CommitImportedFile(session, prepared).Ok);

        var entry = session.Manifest.Entries.Single();
        Assert.Equal(_source, session.Resolver.ResolvePath(entry));

        File.Delete(_source);

        var resolved = session.Resolver.ResolvePath(entry);
        Assert.True(resolved is null || !File.Exists(resolved),
            "a referenced source that has moved must not resolve to an existing file");
    }

    [Fact]
    public void AReferencedSourceThatMoved_IsRefusedByTheExportPreflight()
    {
        // WP-154 built the preflight for exactly this; WP-160 is what makes it reachable by a
        // referenced entry. Asserting the resolver alone would not show the export actually refuses.
        var session = ProjectSession.CreateNew();
        var prepared = Prepare(_package, _source, reference: true);
        Assert.True(MediaLibraryCommands.CommitImportedFile(session, prepared).Ok);
        var entry = session.Manifest.Entries.Single();
        session.ActiveTimeline.Tracks[0].Clips.Add(new Clip
        {
            MediaRef = entry.Id,
            MediaType = ClipType.video,
            SourceClipType = ClipType.video,
            StartFrame = 0,
            DurationFrames = 60,
        });

        var online = Export.ExportMediaPreflight.Inspect(
            session.ActiveTimeline, session.Manifest, session.Resolver);
        Assert.False(online.HasProblems, online.Sentence);

        File.Delete(_source);

        var offline = Export.ExportMediaPreflight.Inspect(
            session.ActiveTimeline, session.Manifest, session.Resolver);
        Assert.True(offline.HasProblems);
        Assert.Contains("will not appear in the export", offline.Sentence);
    }

    // --- WP-160 T1 spot-check finding (Grok 4.6 xhigh, 2026-09-04) --------------------
    // Both contracts said source.reference is ignored for url/bytes/matte. bytes and matte return
    // before the flag is read, so they did. url did not: the download set `path` to a temp file under
    // temno-import-*, the flag was honoured, and DeleteDownloadedImportSource removed that directory
    // in the enclosing finally -- a successful import whose file was already gone.

    [Fact]
    public void Reference_IsHonoured_ForALocalPath()
    {
        var source = new JsonObject { ["path"] = @"C:\media\clip.mp4", ["reference"] = true };
        Assert.True(ToolExecutor.ShouldReferenceInPlace(source, downloadedSource: null));
    }

    [Fact]
    public void Reference_IsIgnored_WhenTheImportDownloadedItsOwnTempFile()
    {
        // The exact reported case: url + reference:true must not write external.absolutePath at a
        // temp file this same call is about to delete.
        var source = new JsonObject { ["url"] = "https://example.invalid/clip.mp4", ["reference"] = true };
        Assert.False(ToolExecutor.ShouldReferenceInPlace(
            source, downloadedSource: @"C:\Temp\temno-import-abc\clip.mp4"));
    }

    [Fact]
    public void Reference_IsFalse_WhenAbsentOrNotTrue()
    {
        Assert.False(ToolExecutor.ShouldReferenceInPlace(new JsonObject { ["path"] = "x" }, null));
        Assert.False(ToolExecutor.ShouldReferenceInPlace(
            new JsonObject { ["path"] = "x", ["reference"] = false }, null));
        Assert.False(ToolExecutor.ShouldReferenceInPlace(null, null));
    }

    private static MediaManifestEntry Clone(MediaManifestEntry source, string? folderId) => new()
    {
        Id = source.Id,
        Name = source.Name,
        Type = source.Type,
        Source = source.Source,
        Duration = source.Duration,
        SourceWidth = source.SourceWidth,
        SourceHeight = source.SourceHeight,
        SourceFps = source.SourceFps,
        HasAudio = source.HasAudio,
        FolderId = folderId,
    };
}
