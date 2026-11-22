using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Temno.Core.Models;
using Temno.Core.Session;
using Temno.Generation;
using Temno.Mcp;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// WP-169. <c>import_media</c> held the MCP call open for the whole download or copy. A URL import,
/// and a large local copy, now add a placeholder with <c>generationStatus: "downloading"</c>, return
/// <c>{mediaRef, status: "downloading"}</c> at once and finish in the background, as the Mac does.
/// Every test observes the job through the MCP surface: <c>get_media ids:[mediaRef]</c>.
/// </summary>
[Collection("EnvRegistry")]
public sealed class QueuedImportTests : IDisposable
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "temno-wp169-" + Guid.NewGuid().ToString("N"));
    private readonly string _projectsDir;
    private int _downloads;

    public QueuedImportTests()
    {
        _projectsDir = Path.Combine(_root, "Projects");
        Directory.CreateDirectory(_projectsDir);
        Environment.SetEnvironmentVariable("TEMNO_PROJECTS_DIR", _projectsDir);
        Environment.SetEnvironmentVariable("TEMNO_REGISTRY_PATH", Path.Combine(_root, "registry.json"));
    }

    public void Dispose()
    {
        ToolExecutor.ImportDownloadOverride = null;
        ToolExecutor.QueuedCopyThresholdBytes = ToolExecutor.DefaultQueuedCopyThresholdBytes;
        ProjectHub.ActiveSession = null;
        Environment.SetEnvironmentVariable("TEMNO_PROJECTS_DIR", null);
        Environment.SetEnvironmentVariable("TEMNO_REGISTRY_PATH", null);
        try { Directory.Delete(_root, recursive: true); } catch { /* temp */ }
    }

    [Fact]
    public void Url_import_returns_downloading_at_once_then_completes()
    {
        var session = NewProject("QueuedUrl");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ServeDownloads(async (_, destination, token) =>
        {
            await gate.Task.WaitAsync(token).ConfigureAwait(false);
            return await WriteAsync(destination, Png, "image/png").ConfigureAwait(false);
        });

        var queued = Call("import_media", new JsonObject
        {
            ["source"] = new JsonObject { ["url"] = "https://media.example/stills/harbor.png" },
        });

        Assert.Equal("downloading", queued["status"]!.GetValue<string>());
        Assert.Equal("image", queued["type"]!.GetValue<string>());
        Assert.Equal("harbor", queued["name"]!.GetValue<string>());
        var mediaRef = queued["mediaRef"]!.GetValue<string>();
        var id = FullId(session, mediaRef);

        // Second call while the bytes are still arriving: the placeholder is in the library.
        var pending = Asset(mediaRef);
        Assert.Equal("downloading", pending["generationStatus"]!.GetValue<string>());
        var pendingOnly = Call("get_media", new JsonObject { ["pending"] = true });
        Assert.Single(pendingOnly["assets"]!.AsArray());
        Assert.Null(pendingOnly["timelines"]);
        var placeholder = session.Manifest.Entries.Single(e => e.Id == id);
        Assert.Equal("https://media.example/stills/harbor.png", placeholder.ImportInput!.SourceUrl);
        Assert.False(File.Exists(session.Resolver.ResolvePath(placeholder)));

        gate.SetResult();
        var ready = WaitSettled(mediaRef, id);

        Assert.Null(ready["generationStatus"]);
        Assert.Equal(1, ready["width"]!.GetValue<int>());
        Assert.Empty(Call("get_media", new JsonObject { ["pending"] = true })["assets"]!.AsArray());
        var landed = session.Manifest.Entries.Single(e => e.Id == id);
        Assert.Null(landed.ImportInput);
        Assert.Equal(Png, File.ReadAllBytes(session.Resolver.ResolvePath(landed)!));
        Assert.Empty(StagingFiles(session));

        var reopened = ProjectSession.Open(session.PackagePath!).Manifest.Entries.Single(e => e.Id == id);
        Assert.Null(reopened.GenerationStatus);
        Assert.Null(reopened.ImportInput);
        Assert.Equal(1, reopened.SourceWidth);
    }

    [Fact]
    public void Failed_download_leaves_a_failed_placeholder_and_no_file()
    {
        var session = NewProject("FailedUrl");
        ServeDownloads((_, _, _) => throw new HttpRequestException("Response status code does not indicate success: 404 (Not Found)."));

        var queued = Call("import_media", new JsonObject
        {
            ["source"] = new JsonObject { ["url"] = "https://media.example/gone.mp4" },
            ["name"] = "Gone",
        });
        var mediaRef = queued["mediaRef"]!.GetValue<string>();
        var failed = WaitSettled(mediaRef, FullId(session, mediaRef));

        Assert.Equal(
            "failed: Response status code does not indicate success: 404 (Not Found).",
            failed["generationStatus"]!.GetValue<string>());
        Assert.Single(Call("get_media", new JsonObject { ["pending"] = true })["assets"]!.AsArray());
        Assert.Empty(MediaFiles(session));
        Assert.Empty(StagingFiles(session));
        // Kept, as on the Mac: the reason stays readable until someone deletes the asset.
        Assert.NotNull(session.Manifest.Entries.Single().ImportInput);
    }

    [Fact]
    public void Bytes_that_do_not_decode_fail_the_job_and_remove_the_landed_file()
    {
        var session = NewProject("UndecodableUrl");
        ServeDownloads((_, destination, _) => WriteAsync(destination, "not a media container"u8.ToArray(), "video/mp4"));

        var queued = Call("import_media", new JsonObject
        {
            ["source"] = new JsonObject { ["url"] = "https://media.example/clip.mp4" },
        });
        var mediaRef = queued["mediaRef"]!.GetValue<string>();
        var failed = WaitSettled(mediaRef, FullId(session, mediaRef));

        Assert.StartsWith("failed: media validation failed", failed["generationStatus"]!.GetValue<string>());
        Assert.Empty(MediaFiles(session));
    }

    [Fact]
    public void Url_whose_type_only_the_response_can_tell_still_downloads_inline()
    {
        var session = NewProject("InlineUrl");
        ServeDownloads((_, destination, _) => WriteAsync(destination, Png, "image/png"));

        var ready = Call("import_media", new JsonObject
        {
            ["source"] = new JsonObject { ["url"] = "https://media.example/signed?sig=abc" },
        });

        Assert.Equal("ready", ready["status"]!.GetValue<string>());
        Assert.Equal(ready["id"]!.GetValue<string>(), ready["mediaRef"]!.GetValue<string>());
        var entry = Assert.Single(session.Manifest.Entries);
        Assert.Null(entry.GenerationStatus);
        Assert.Equal(1, entry.SourceWidth);
    }

    [Fact]
    public void Unsupported_type_is_refused_before_any_download()
    {
        var session = NewProject("RefusedUrl");
        ServeDownloads((_, destination, _) => WriteAsync(destination, Png, "image/png"));

        var extension = Fail("import_media", new JsonObject
        {
            ["source"] = new JsonObject { ["url"] = "https://media.example/watch.php" },
        });
        var mime = Fail("import_media", new JsonObject
        {
            ["source"] = new JsonObject { ["url"] = "https://media.example/page", ["mimeType"] = "text/html" },
        });

        Assert.Contains("unsupported file extension '.php'", extension);
        Assert.Contains("unsupported source.mimeType 'text/html'", mime);
        Assert.Equal(0, _downloads);
        Assert.Empty(session.Manifest.Entries);
    }

    [Fact]
    public void Large_local_copy_queues_and_small_copy_stays_inline()
    {
        var session = NewProject("LocalCopies");
        ToolExecutor.QueuedCopyThresholdBytes = 1024;
        var small = WriteSource("small.png", Png);
        var large = WriteSource("large.png", [.. Png, .. new byte[2048]]);

        var inline = Call("import_media", new JsonObject { ["source"] = new JsonObject { ["path"] = small } });
        var queued = Call("import_media", new JsonObject { ["source"] = new JsonObject { ["path"] = large } });

        Assert.Equal("ready", inline["status"]!.GetValue<string>());
        Assert.Equal("downloading", queued["status"]!.GetValue<string>());
        var mediaRef = queued["mediaRef"]!.GetValue<string>();
        var id = FullId(session, mediaRef);
        // Probed before queuing, so the placeholder already knows what it is.
        Assert.Equal(1, session.Manifest.Entries.Single(e => e.Id == id).SourceWidth);

        Assert.Null(WaitSettled(mediaRef, id)["generationStatus"]);
        var copied = session.Manifest.Entries.Single(e => e.Id == id);
        Assert.Equal(File.ReadAllBytes(large), File.ReadAllBytes(session.Resolver.ResolvePath(copied)!));
        Assert.True(File.Exists(large));
        Assert.Empty(StagingFiles(session));
    }

    [Fact]
    public void Large_corrupt_or_referenced_files_never_queue()
    {
        var session = NewProject("NeverQueued");
        ToolExecutor.QueuedCopyThresholdBytes = 1;
        var corrupt = WriteSource("corrupt.mp4", "not a media container"u8.ToArray());
        var linked = WriteSource("linked.png", Png);

        var refused = Fail("import_media", new JsonObject { ["source"] = new JsonObject { ["path"] = corrupt } });
        var reference = Call("import_media", new JsonObject
        {
            ["source"] = new JsonObject { ["path"] = linked, ["reference"] = true },
        });

        Assert.Contains("media validation failed", refused);
        Assert.Equal("ready", reference["status"]!.GetValue<string>());
        var entry = Assert.Single(session.Manifest.Entries);
        Assert.IsType<MediaSource.ExternalSource>(entry.Source);
        Assert.Empty(MediaFiles(session));
    }

    [Fact]
    public void Cancel_marks_the_placeholder_cancelled()
    {
        var session = NewProject("CancelledUrl");
        ServeDownloads(async (_, destination, token) =>
        {
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            return await WriteAsync(destination, Png, "image/png").ConfigureAwait(false);
        });

        var mediaRef = Call("import_media", new JsonObject
        {
            ["source"] = new JsonObject { ["url"] = "https://media.example/slow.png" },
        })["mediaRef"]!.GetValue<string>();
        var id = FullId(session, mediaRef);

        Assert.True(ToolExecutor.CancelInFlightGeneration(id));
        Assert.Equal("failed: Cancelled", WaitSettled(mediaRef, id)["generationStatus"]!.GetValue<string>());
        Assert.Empty(MediaFiles(session));
        Assert.Empty(StagingFiles(session));
    }

    [Fact]
    public void Undo_while_downloading_removes_the_import_and_discards_the_bytes()
    {
        var session = NewProject("UndoneUrl");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ServeDownloads(async (_, destination, token) =>
        {
            await gate.Task.WaitAsync(token).ConfigureAwait(false);
            return await WriteAsync(destination, Png, "image/png").ConfigureAwait(false);
        });

        var mediaRef = Call("import_media", new JsonObject
        {
            ["source"] = new JsonObject { ["url"] = "https://media.example/undone.png" },
        })["mediaRef"]!.GetValue<string>();
        var id = FullId(session, mediaRef);
        Call("undo", []);
        Assert.Empty(session.Manifest.Entries);

        gate.SetResult();
        WaitUntil(() => !ToolExecutor.IsImportRunning(id));

        Assert.Empty(session.Manifest.Entries);
        Assert.Empty(MediaFiles(session));
        Assert.Empty(StagingFiles(session));
    }

    [Fact]
    public void A_job_lands_on_the_reopened_session_not_the_closed_one()
    {
        var first = NewProject("ReopenedUrl");
        var package = first.PackagePath!;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ServeDownloads(async (_, destination, token) =>
        {
            await gate.Task.WaitAsync(token).ConfigureAwait(false);
            return await WriteAsync(destination, Png, "image/png").ConfigureAwait(false);
        });

        var mediaRef = Call("import_media", new JsonObject
        {
            ["source"] = new JsonObject { ["url"] = "https://media.example/reopened.png" },
        })["mediaRef"]!.GetValue<string>();
        var id = FullId(first, mediaRef);
        Call("close_project", []);
        Call("open_project", new JsonObject { ["path"] = package });
        var second = Assert.IsType<ProjectSession>(ProjectHub.ActiveSession);
        Assert.NotSame(first, second);
        // Still running here, so opening must not call it interrupted.
        Assert.Equal("downloading", second.Manifest.Entries.Single(e => e.Id == id).GenerationStatus);

        gate.SetResult();
        Assert.Null(WaitSettled(mediaRef, id)["generationStatus"]);

        Assert.Null(second.Manifest.Entries.Single(e => e.Id == id).GenerationStatus);
        Assert.Null(ProjectSession.Open(package).Manifest.Entries.Single(e => e.Id == id).GenerationStatus);
    }

    [Fact]
    public void Opening_settles_imports_that_no_job_owns()
    {
        var package = Path.Combine(_projectsDir, "Interrupted.temno");
        var session = ProjectSession.CreateNew();
        session.Save(package);
        Directory.CreateDirectory(Path.Combine(package, "media"));
        File.WriteAllBytes(Path.Combine(package, "media", "imported-aaaaaaaa.png"), Png);
        session.Manifest.Entries.Add(Placeholder("AAAAAAAA-0000-0000-0000-000000000001", "media/imported-aaaaaaaa.png"));
        session.Manifest.Entries.Add(Placeholder("BBBBBBBB-0000-0000-0000-000000000002", "media/imported-bbbbbbbb.png"));
        session.Save();

        Call("open_project", new JsonObject { ["path"] = package });

        var landed = WaitSettled("AAAAAAAA", "AAAAAAAA-0000-0000-0000-000000000001");
        Assert.Null(landed["generationStatus"]);
        Assert.Equal(1, landed["width"]!.GetValue<int>());
        Assert.Equal(ToolExecutor.ImportInterruptedStatus, Asset("BBBBBBBB")["generationStatus"]!.GetValue<string>());
        var reopened = ProjectSession.Open(package).Manifest.Entries;
        Assert.Null(reopened.Single(e => e.Id.StartsWith("AAAAAAAA", StringComparison.Ordinal)).ImportInput);
        Assert.Equal(
            ToolExecutor.ImportInterruptedStatus,
            reopened.Single(e => e.Id.StartsWith("BBBBBBBB", StringComparison.Ordinal)).GenerationStatus);
    }

    // T1 finding 1: the job captured session A; the project was reopened as B, edited, saved and
    // closed. Landing on A would write A's stale project over B's saved edit.
    [Fact]
    public void A_job_that_outlives_its_session_lands_on_the_saved_project()
    {
        var first = NewProject("OutlivedUrl");
        var package = first.PackagePath!;
        var gate = GatedDownloads();

        var mediaRef = QueueUrl("https://media.example/outlived.png");
        var id = FullId(first, mediaRef);
        Call("close_project", []);
        Call("open_project", new JsonObject { ["path"] = package });
        Call("add_marker", new JsonObject { ["frame"] = 0, ["name"] = "Saved by B" });
        Call("close_project", []);

        gate.SetResult();
        WaitUntil(() => !ToolExecutor.IsImportRunning(id));

        var saved = ProjectSession.Open(package);
        Assert.Contains(saved.ActiveTimeline.Markers, m => m.Name == "Saved by B");
        var entry = saved.Manifest.Entries.Single(e => e.Id == id);
        Assert.Null(entry.GenerationStatus);
        Assert.True(File.Exists(saved.Resolver.ResolvePath(entry)));
    }

    // T1 finding 2: Save As moves the session to a new package while the bytes are still arriving.
    [Fact]
    public void Save_as_during_a_download_lands_the_file_in_the_new_package()
    {
        var session = NewProject("SaveAsUrl");
        var gate = GatedDownloads();
        var mediaRef = QueueUrl("https://media.example/moved.png");
        var id = FullId(session, mediaRef);

        var moved = Path.Combine(_projectsDir, "SaveAsUrl copy.temno");
        ProjectHub.RunSerialized(() => session.Save(moved));
        gate.SetResult();
        var ready = WaitSettled(mediaRef, id);

        Assert.Null(ready["generationStatus"]);
        var entry = session.Manifest.Entries.Single(e => e.Id == id);
        var path = session.Resolver.ResolvePath(entry)!;
        Assert.StartsWith(moved, path, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Png, File.ReadAllBytes(path));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_projectsDir, ".temno-import-*"));
    }

    // T1 finding 3: undoing an edit made during the download restores the pending placeholder.
    [Fact]
    public void Undo_of_an_edit_made_mid_download_does_not_leave_it_pending()
    {
        var session = NewProject("UndoAfterLanding");
        var gate = GatedDownloads();
        var mediaRef = QueueUrl("https://media.example/landed.png");
        var id = FullId(session, mediaRef);
        Call("add_marker", new JsonObject { ["frame"] = 0 });
        gate.SetResult();
        Assert.Null(WaitSettled(mediaRef, id)["generationStatus"]);

        Call("undo", []);

        var asset = Asset(mediaRef);
        Assert.Null(asset["generationStatus"]);
        Assert.Equal(1, asset["width"]!.GetValue<int>());
        Assert.Null(session.Manifest.Entries.Single(e => e.Id == id).ImportInput);
    }

    // T1 finding 4: a failed save while settling must not strand the other imports' claims.
    [Fact]
    public void A_failed_settle_save_leaves_no_import_claimed()
    {
        var package = InterruptedPackage("SettleSaveFails");
        Call("open_project", new JsonObject { ["path"] = package });
        WaitUntil(() => !ToolExecutor.IsImportRunning(LandedId) && !ToolExecutor.IsImportRunning(MissingId));
        var session = Assert.IsType<ProjectSession>(ProjectHub.ActiveSession);
        foreach (var entry in session.Manifest.Entries)
        {
            entry.GenerationStatus = ToolExecutor.ImportDownloadingStatus;
            entry.ImportInput ??= new MediaImportInput();
        }

        using (LockManifest(package))
        {
            ToolExecutor.ResolveInterruptedImports(session);
            WaitUntil(() => !ToolExecutor.IsImportRunning(LandedId) && !ToolExecutor.IsImportRunning(MissingId));
        }

        Assert.Null(session.Manifest.Entries.Single(e => e.Id == LandedId).GenerationStatus);
        Assert.Equal(
            ToolExecutor.ImportInterruptedStatus,
            session.Manifest.Entries.Single(e => e.Id == MissingId).GenerationStatus);
        Assert.True(session.IsDirty);
    }

    // T1 finding 5: a save that fails turned a good landing into a failure without its importInput,
    // and deleted the bytes. A failed save is not a failed import: the result stays, unsaved, like
    // any other edit, and the next save writes it.
    [Fact]
    public void A_landing_that_cannot_be_saved_yet_keeps_its_bytes_and_saves_later()
    {
        var session = NewProject("UnsavableLanding");
        var gate = GatedDownloads();
        var mediaRef = QueueUrl("https://media.example/unsaved.png");
        var id = FullId(session, mediaRef);

        using (LockManifest(session.PackagePath!))
        {
            gate.SetResult();
            WaitUntil(() => !ToolExecutor.IsImportRunning(id));
        }

        var entry = session.Manifest.Entries.Single(e => e.Id == id);
        Assert.Null(entry.GenerationStatus);
        Assert.True(session.IsDirty);
        Assert.Equal(Png, File.ReadAllBytes(session.Resolver.ResolvePath(entry)!));
        ProjectHub.RunSerialized(() => session.Save());
        Assert.Null(ProjectSession.Open(session.PackagePath!).Manifest.Entries.Single(e => e.Id == id).GenerationStatus);
    }

    // Found in self-review: settling must never delete a file the package does not own.
    [Fact]
    public void Settling_never_deletes_a_file_outside_the_package()
    {
        var package = Path.Combine(_projectsDir, "ExternalPlaceholder.temno");
        var session = ProjectSession.CreateNew();
        session.Save(package);
        var usersFile = WriteSource("users-own.mp4", "not a media container"u8.ToArray());
        var placeholder = Placeholder("CCCCCCCC-0000-0000-0000-000000000003", "unused");
        placeholder.Source = MediaSource.External(usersFile);
        session.Manifest.Entries.Add(placeholder);
        session.Save();

        // WP-399: the package names a file outside itself that nobody here allowed. Opening it must not
        // settle the entry, rewrite media.json, or touch the file.
        var manifestPath = Path.Combine(package, Temno.Core.Project.ProjectConstants.ManifestFilename);
        var written = File.ReadAllBytes(manifestPath);
        Call("open_project", new JsonObject { ["path"] = package });
        Assert.False(ToolExecutor.IsImportRunning(placeholder.Id));
        Assert.Equal(ToolExecutor.ImportDownloadingStatus, Asset("CCCCCCCC")["generationStatus"]!.GetValue<string>());
        Call("close_project", new JsonObject());
        Assert.Equal(written, File.ReadAllBytes(manifestPath));
        Assert.True(File.Exists(usersFile));

        // Once the user allows the path, the next open settles it as before, and still never deletes it.
        Assert.True(ProjectSession.Open(package).ExternalMedia.Allow(usersFile));
        Call("open_project", new JsonObject { ["path"] = package });
        var settled = WaitSettled("CCCCCCCC", placeholder.Id);

        Assert.StartsWith("failed: media validation failed", settled["generationStatus"]!.GetValue<string>());
        Assert.True(File.Exists(usersFile));
    }

    [Fact]
    public void Inline_responses_keep_the_entry_and_add_status_ready()
    {
        NewProject("InlineShape");

        var bytes = Call("import_media", new JsonObject
        {
            ["source"] = new JsonObject { ["bytes"] = Convert.ToBase64String(Png), ["mimeType"] = "image/png" },
        });

        Assert.Equal("ready", bytes["status"]!.GetValue<string>());
        Assert.Equal(bytes["id"]!.GetValue<string>(), bytes["mediaRef"]!.GetValue<string>());
        Assert.NotNull(bytes["source"]);
        Assert.NotNull(bytes["sourceWidth"]);
    }

    private const string LandedId = "DDDDDDDD-0000-0000-0000-000000000004";
    private const string MissingId = "EEEEEEEE-0000-0000-0000-000000000005";

    /// <summary>A saved package holding two unowned import placeholders, one with its file landed.</summary>
    private string InterruptedPackage(string name)
    {
        var package = Path.Combine(_projectsDir, name + ".temno");
        var session = ProjectSession.CreateNew();
        session.Save(package);
        Directory.CreateDirectory(Path.Combine(package, "media"));
        File.WriteAllBytes(Path.Combine(package, "media", "imported-dddddddd.png"), Png);
        session.Manifest.Entries.Add(Placeholder(LandedId, "media/imported-dddddddd.png"));
        session.Manifest.Entries.Add(Placeholder(MissingId, "media/imported-eeeeeeee.png"));
        session.Save();
        return package;
    }

    /// <summary>Holds media.json open so the next in-place save of the package fails.</summary>
    private static FileStream LockManifest(string package)
        => new(Path.Combine(package, "media.json"), FileMode.Open, FileAccess.Read, FileShare.None);

    private TaskCompletionSource GatedDownloads()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ServeDownloads(async (_, destination, token) =>
        {
            await gate.Task.WaitAsync(token).ConfigureAwait(false);
            return await WriteAsync(destination, Png, "image/png").ConfigureAwait(false);
        });
        return gate;
    }

    private static string QueueUrl(string url)
        => Call("import_media", new JsonObject { ["source"] = new JsonObject { ["url"] = url } })["mediaRef"]!
            .GetValue<string>();

    private static MediaManifestEntry Placeholder(string id, string relativePath) => new()
    {
        Id = id,
        Name = id[..8],
        Type = ClipType.image,
        Source = MediaSource.Project(relativePath),
        GenerationStatus = ToolExecutor.ImportDownloadingStatus,
        ImportInput = new MediaImportInput { SourceUrl = "https://media.example/" + id[..8] + ".png" },
    };

    private ProjectSession NewProject(string name)
    {
        Call("new_project", new JsonObject { ["name"] = name });
        return Assert.IsType<ProjectSession>(ProjectHub.ActiveSession);
    }

    private void ServeDownloads(Func<Uri, string, CancellationToken, Task<RemoteDownloadResult>> download)
        => ToolExecutor.ImportDownloadOverride = (uri, destination, token) =>
        {
            Interlocked.Increment(ref _downloads);
            return download(uri, destination, token);
        };

    private static async Task<RemoteDownloadResult> WriteAsync(string destination, byte[] bytes, string contentType)
    {
        await File.WriteAllBytesAsync(destination, bytes).ConfigureAwait(false);
        return new RemoteDownloadResult(new Uri("https://media.example/"), contentType, bytes.Length);
    }

    private string WriteSource(string name, byte[] bytes)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, bytes);
        return path;
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

    private static JsonObject Asset(string mediaRef)
        => Call("get_media", new JsonObject { ["ids"] = new JsonArray(mediaRef) })["assets"]!
            .AsArray().Single()!.AsObject();

    private static string FullId(ProjectSession session, string mediaRef)
        => session.Manifest.Entries.Single(e => e.Id.StartsWith(mediaRef, StringComparison.OrdinalIgnoreCase)).Id;

    /// <summary>Polls the way an MCP client would, until the job has let go of the placeholder.</summary>
    private static JsonObject WaitSettled(string mediaRef, string id)
    {
        WaitUntil(() => !ToolExecutor.IsImportRunning(id)
            && Asset(mediaRef)["generationStatus"]?.GetValue<string>() != ToolExecutor.ImportDownloadingStatus);
        return Asset(mediaRef);
    }

    private static void WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The queued import did not settle within 30 seconds.");
            Thread.Sleep(20);
        }
    }

    private static IEnumerable<string> MediaFiles(ProjectSession session)
    {
        var media = Path.Combine(session.PackagePath!, "media");
        return Directory.Exists(media)
            ? Directory.EnumerateFiles(media).Where(f => !Path.GetFileName(f).StartsWith('.'))
            : [];
    }

    private static IEnumerable<string> StagingFiles(ProjectSession session)
    {
        var media = Path.Combine(session.PackagePath!, "media");
        return Directory.Exists(media) ? Directory.EnumerateFiles(media, ".*.tmp") : [];
    }
}
