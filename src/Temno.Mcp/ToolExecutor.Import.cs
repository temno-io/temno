using System.Text.Json;
using System.Text.Json.Nodes;
using Temno.Core.IO;
using Temno.Core.Models;
using Temno.Core.Project;
using Temno.Core.Session;
using Temno.Generation;

namespace Temno.Mcp;

// WP-169: import_media queues what would hold the call open. Mac ToolExecutor+Import.swift adds a
// placeholder asset with generationStatus "downloading" and importInput, returns
// {mediaRef, type, status: "downloading"}, and finishes in the background; the caller polls
// get_media with ids:[mediaRef] until generationStatus clears, or reads "failed: <reason>".

public static partial class ToolExecutor
{
    public const string ImportDownloadingStatus = "downloading";
    public const string ImportInterruptedStatus = "failed: Import interrupted";

    /// <summary>
    /// A local copy at least this large queues. Smaller copies finish inside the call, as they always
    /// have: at typical disk speeds they take well under a second, and a ready entry is simpler for the
    /// caller than a placeholder. The Mac queues every copy.
    /// </summary>
    public const long DefaultQueuedCopyThresholdBytes = 64L * 1024 * 1024;

    public static long QueuedCopyThresholdBytes { get; set; } = DefaultQueuedCopyThresholdBytes;

    /// <summary>Test seam for the network fetch of a <c>source.url</c> import, queued or inline.</summary>
    public static Func<Uri, string, CancellationToken, Task<RemoteDownloadResult>>? ImportDownloadOverride { get; set; }

    /// <summary>Import ids with a job in this process. Guarded by <c>_genLock</c>.</summary>
    private static readonly HashSet<string> _runningImports = new(StringComparer.Ordinal);

    /// <summary>
    /// Queues a URL import when its media type is known before the first byte arrives: from
    /// <c>source.mimeType</c>, or from the URL path's extension. Returns null when only the response's
    /// Content-Type can say what the file is; that import downloads inside the call, as before, because
    /// a placeholder's type must be right from the start (it can be placed on the timeline).
    /// </summary>
    private static object? TryQueueUrlImport(
        ProjectSession session,
        string url,
        string? mimeOverride,
        string? name,
        string? folderId)
    {
        var uri = ValidateImportUrl(url);
        string extension;
        ClipType type;
        if (!string.IsNullOrWhiteSpace(mimeOverride))
        {
            if (!MediaLibraryCommands.TryGetMimeType(mimeOverride, out extension, out type))
                throw new InvalidOperationException($"import_media: unsupported source.mimeType '{mimeOverride}'");
        }
        else
        {
            var urlExtension = Path.GetExtension(uri.LocalPath).ToLowerInvariant();
            if (string.IsNullOrEmpty(urlExtension))
                return null;
            // The inline path would download this and then refuse it with the same message.
            if (!MediaLibraryCommands.TryGetTypeFromExtension(urlExtension, out type))
                throw new InvalidOperationException($"unsupported file extension '{urlExtension}'");
            extension = urlExtension.TrimStart('.');
        }

        var displayName = name ?? Path.GetFileNameWithoutExtension(
            MakeSafeDownloadFilename(Path.GetFileName(uri.LocalPath), "." + extension));
        return QueueImport(
            session,
            type,
            extension,
            displayName,
            folderId,
            new MediaImportInput { SourceUrl = uri.AbsoluteUri, CreatedAt = DateTimeOffset.UtcNow },
            probed: null,
            (staging, token) => DownloadImportAsync(uri, staging, token));
    }

    /// <summary>
    /// Queues a package copy of a large local file. The source is validated and probed first, so a
    /// missing, unsupported or corrupt file is still refused by the call itself; only the byte copy
    /// runs in the background. Returns null for a file the inline path should handle.
    /// </summary>
    private static object? TryQueueCopyImport(
        ProjectSession session,
        string sourcePath,
        string? name,
        string? folderId)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(sourcePath);
            if (!info.Exists || info.Length < QueuedCopyThresholdBytes)
                return null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var extension = info.Extension.ToLowerInvariant();
        if (!MediaLibraryCommands.TryGetTypeFromExtension(extension, out var type))
            return null;

        var probed = new MediaManifestEntry { Type = type };
        ValidateAndProbeImportedMediaMetadata(probed, info.FullName);
        return QueueImport(
            session,
            type,
            extension.TrimStart('.'),
            name ?? Path.GetFileNameWithoutExtension(info.FullName),
            folderId,
            new MediaImportInput { SourcePath = info.FullName, CreatedAt = DateTimeOffset.UtcNow },
            probed,
            (staging, token) => CopyImportAsync(info.FullName, staging, token));
    }

    private static object QueueImport(
        ProjectSession session,
        ClipType type,
        string extension,
        string name,
        string? folderId,
        MediaImportInput importInput,
        MediaManifestEntry? probed,
        Func<string, CancellationToken, Task> fetch)
    {
        var id = Guid.NewGuid().ToString().ToUpperInvariant();
        var filename = $"imported-{id[..8].ToLowerInvariant()}.{extension}";
        var placeholder = new MediaManifestEntry
        {
            Id = id,
            Name = name,
            Type = type,
            Source = MediaSource.Project($"{ProjectConstants.MediaDirectoryName}/{filename}"),
            FolderId = folderId,
            GenerationStatus = ImportDownloadingStatus,
            ImportInput = importInput,
        };
        if (probed is not null)
            CopyProbedMetadata(probed, placeholder);

        // Claimed before the placeholder is visible, so ResolveInterruptedImports never sees it unowned.
        TryClaimImport(id);
        string packagePath;
        try
        {
            packagePath = ProjectHub.RunSerialized(() =>
            {
                lock (_sessionLock)
                {
                    if (session.ManifestUnreadable)
                        throw new InvalidOperationException("media manifest is unreadable; save a compatible project before adding media");
                    var package = session.PackagePath;
                    if (string.IsNullOrWhiteSpace(package))
                        throw new InvalidOperationException("open a project package before importing media");
                    if (!MediaLibraryCommands.FolderExistsOrRoot(session.Manifest, folderId))
                        throw new InvalidOperationException($"destination folder not found: {folderId}");

                    // One undo step, like an inline import. Saved at once so a quit mid-download
                    // leaves a record that reopens as "Import interrupted" rather than nothing.
                    session.RecordBeforeEdit();
                    session.Manifest.Entries.Add(placeholder);
                    session.MarkDirty();
                    TrySave(session);
                    return package;
                }
            });
        }
        catch
        {
            ReleaseImport(id);
            throw;
        }

        // Staged beside the package, not in it: Save As clones every file in the package, and the
        // bytes may belong to a different package by the time they land.
        var stagingDirectory = Path.Combine(
            Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(packagePath)))!,
            $".temno-import-{Guid.NewGuid():N}.partial");
        StartFetchJob(session, id, type, $"{ProjectConstants.MediaDirectoryName}/{filename}", stagingDirectory, fetch, probed);

        return new Dictionary<string, object>
        {
            ["mediaRef"] = id,
            ["name"] = name,
            ["type"] = type.ToString(),
            ["status"] = ImportDownloadingStatus,
            ["note"] = (importInput.SourceUrl is not null ? "Downloading" : "Copying")
                + " in the background. Poll get_media with ids:[mediaRef] until generationStatus clears;"
                + " 'failed: <reason>' means it did not land.",
        };
    }

    /// <summary>
    /// Settles import placeholders that are pending with no job in this process: after a quit or
    /// crash mid-import, or an undo that restored one after its job ended. An outcome this process
    /// already reached is applied again; a placeholder whose file is there is probed and finished;
    /// one without becomes "failed: Import interrupted", as the Mac writes on open. Runs on every
    /// editor bind, <c>open_project</c>, <c>get_media</c>, and MCP <c>undo</c>/<c>redo</c>.
    /// </summary>
    public static void ResolveInterruptedImports(ProjectSession? session)
    {
        if (session is null || string.IsNullOrEmpty(session.PackagePath))
            return;

        var candidates = ProjectHub.RunSerialized(() =>
        {
            lock (_sessionLock)
            {
                // WP-399: an entry naming a file outside the project the user has not allowed is left
                // exactly as written: opening a project must not rewrite it, or settle it, before consent.
                return session.Manifest.Entries
                    .Where(IsPendingImport)
                    .Where(e => !session.Resolver.IsBlocked(e))
                    .Select(e => (e.Id, e.Type, Path: session.Resolver.ResolvePath(e), Entry: e))
                    .ToList();
            }
        });
        // Settling probes the file: an allowed external one must still lead where it did when the user
        // allowed it. Checked here, outside the gate; one that does not is left as written too.
        candidates = candidates
            .Where(c => session.Resolver.ExternalState(c.Entry) == ExternalMediaState.Allowed)
            .ToList();
        if (candidates.Count == 0)
            return;

        // Claimed outside the session lock: _genLock is never taken while _sessionLock is held.
        var reapply = new Dictionary<string, SettledImport>(StringComparer.Ordinal);
        var interrupted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (id, type, path, _) in candidates.Where(c => TryClaimImport(c.Id)))
        {
            var fileIsThere = path is not null && File.Exists(path);
            if (SettledOutcome(id) is { } settled && (!settled.Landed || fileIsThere))
                reapply[id] = settled;
            else if (fileIsThere)
                StartSettleJob(session, id, type, path!);
            else
                interrupted.Add(id);
        }
        if (reapply.Count == 0 && interrupted.Count == 0)
            return;

        try
        {
            ProjectHub.RunSerialized(() =>
            {
                lock (_sessionLock)
                {
                    foreach (var entry in session.Manifest.Entries.Where(IsPendingImport))
                    {
                        if (reapply.TryGetValue(entry.Id, out var settled))
                            settled.Apply(entry);
                        else if (interrupted.Contains(entry.Id))
                            entry.GenerationStatus = ImportInterruptedStatus;
                    }
                    session.MarkDirty();
                    TrySave(session);
                }
            });
        }
        finally
        {
            foreach (var id in reapply.Keys.Concat(interrupted))
                ReleaseImport(id);
        }
    }

    private static bool IsPendingImport(MediaManifestEntry entry)
        => entry.ImportInput is not null && entry.GenerationStatus == ImportDownloadingStatus;

    /// <summary>Test hook: true while an import job for <paramref name="mediaRef"/> is running here.</summary>
    public static bool IsImportRunning(string mediaRef)
    {
        lock (_genLock)
            return _runningImports.Contains(mediaRef);
    }

    private static bool TryClaimImport(string id)
    {
        lock (_genLock)
            return _runningImports.Add(id);
    }

    private static void ReleaseImport(string id)
    {
        lock (_genLock)
            _runningImports.Remove(id);
    }

    /// <summary>An outcome a job reached here, applied again if an undo brings its placeholder back.</summary>
    private sealed record SettledImport(bool Landed, Action<MediaManifestEntry> Apply);

    /// <summary>Guarded by <c>_genLock</c>. One small record per import this process finished.</summary>
    private static readonly Dictionary<string, SettledImport> _settledImports = new(StringComparer.Ordinal);

    private static SettledImport? SettledOutcome(string id)
    {
        lock (_genLock)
            return _settledImports.GetValueOrDefault(id);
    }

    private static void RecordSettled(string id, bool landed, Action<MediaManifestEntry> apply)
    {
        lock (_genLock)
            _settledImports[id] = new SettledImport(landed, apply);
    }

    private enum ImportCommit { Applied, Gone, Moved }

    /// <summary>
    /// Runs one claimed download or copy. <paramref name="fetch"/> writes into
    /// <paramref name="stagingDirectory"/>, beside the package; the complete file is then moved to
    /// <paramref name="relativePath"/> in whichever package the project is in by then, so the package
    /// never holds a partial file under an asset's name. The cancellation source is shared with
    /// generation jobs, so the editor's Cancel reaches it.
    /// </summary>
    private static void StartFetchJob(
        ProjectSession captured,
        string id,
        ClipType type,
        string relativePath,
        string stagingDirectory,
        Func<string, CancellationToken, Task> fetch,
        MediaManifestEntry? knownMetadata)
    {
        var token = RegisterGenerationCancel(id);
        _ = Task.Run(async () =>
        {
            string? landed = null;
            try
            {
                Directory.CreateDirectory(stagingDirectory);
                var staged = Path.Combine(stagingDirectory, Path.GetFileName(relativePath));
                await fetch(staged, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                var ready = ReadyOutcome(knownMetadata ?? ProbeImported(type, staged));

                landed = staged;
                for (var attempt = 1; ; attempt++)
                {
                    // Save As can move the project to another package while the bytes arrive.
                    var package = ProjectHub.RunSerialized(() => captured.PackagePath);
                    var target = package is null ? null : MediaResolver.ResolveProjectPath(package, relativePath);
                    if (target is null)
                        throw new InvalidOperationException("the project's package can no longer be written");
                    if (!PathEquals(target, landed))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        File.Move(landed, target, overwrite: false);
                        landed = target;
                    }

                    var commit = CommitImport(captured, id, package, ready);
                    if (commit == ImportCommit.Moved && attempt < 3)
                        continue;
                    if (commit == ImportCommit.Applied)
                        RecordSettled(id, landed: true, ready);
                    else
                        TryDeleteFile(landed);   // undone or deleted while the bytes were arriving
                    break;
                }
            }
            catch (Exception ex)
            {
                if (landed is not null)
                    TryDeleteFile(landed);
                FailImport(captured, id, token, ex);
            }
            finally
            {
                TryDeleteDirectory(stagingDirectory);
                ReleaseImport(id);
                UnregisterGenerationCancel(id);
            }
        });
    }

    /// <summary>
    /// Finishes a claimed placeholder whose file is already where it points. The file was not written
    /// by this job, so a failure never deletes it: it may be the user's own file.
    /// </summary>
    private static void StartSettleJob(ProjectSession session, string id, ClipType type, string path)
    {
        var token = RegisterGenerationCancel(id);
        _ = Task.Run(() =>
        {
            try
            {
                var ready = ReadyOutcome(ProbeImported(type, path));
                if (CommitImport(session, id, expectedPackage: null, ready) == ImportCommit.Applied)
                    RecordSettled(id, landed: true, ready);
            }
            catch (Exception ex)
            {
                FailImport(session, id, token, ex);
            }
            finally
            {
                ReleaseImport(id);
                UnregisterGenerationCancel(id);
            }
        });
    }

    private static void FailImport(ProjectSession captured, string id, CancellationToken token, Exception ex)
    {
        var reason = token.IsCancellationRequested ? "Cancelled" : ImportFailureReason(ex);
        Action<MediaManifestEntry> failed = entry => entry.GenerationStatus = "failed: " + reason;
        try
        {
            if (CommitImport(captured, id, expectedPackage: null, failed) == ImportCommit.Applied)
                RecordSettled(id, landed: false, failed);
        }
        catch
        {
            // Nowhere left to record it; the placeholder settles as interrupted when next opened.
        }
    }

    /// <summary>
    /// Applies an outcome to the session that owns the package now: the captured one while it is
    /// open, a reopened session of the same package, or else the package as saved on disk. Saving the
    /// captured session after its project closed would write its stale state over later saves.
    /// Returns <see cref="ImportCommit.Moved"/> when the package is no longer
    /// <paramref name="expectedPackage"/> (a Save As since the caller looked).
    /// </summary>
    private static ImportCommit CommitImport(
        ProjectSession captured,
        string id,
        string? expectedPackage,
        Action<MediaManifestEntry> apply)
        => ProjectHub.RunSerialized(() =>
        {
            lock (_sessionLock)
            {
                var active = ProjectHub.ActiveSession;
                ProjectSession session;
                if (ReferenceEquals(active, captured))
                    session = captured;
                else if (active is not null && PathEquals(active.PackagePath, captured.PackagePath))
                    session = active;
                else if (captured.PackagePath is { } package && Directory.Exists(package))
                    session = ProjectSession.Open(package);
                else
                    return ImportCommit.Gone;

                if (expectedPackage is not null && !PathEquals(session.PackagePath, expectedPackage))
                    return ImportCommit.Moved;
                var entry = session.Manifest.Entries.FirstOrDefault(e => e.Id == id);
                if (entry is null)
                    return ImportCommit.Gone;
                apply(entry);
                session.MarkDirty();
                TrySave(session);
                return ImportCommit.Applied;
            }
        });

    private static Action<MediaManifestEntry> ReadyOutcome(MediaManifestEntry metadata)
        => entry =>
        {
            CopyProbedMetadata(metadata, entry);
            entry.GenerationStatus = null;
            entry.ImportInput = null;
        };

    private static MediaManifestEntry ProbeImported(ClipType type, string path)
    {
        var probed = new MediaManifestEntry { Type = type };
        ValidateAndProbeImportedMediaMetadata(probed, path);
        return probed;
    }

    /// <summary>
    /// A failed save is an unsaved edit, not a failed import: the outcome stays in the session, which
    /// stays dirty, and the next save or autosave writes it.
    /// </summary>
    private static void TrySave(ProjectSession session)
    {
        try
        {
            session.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void CopyProbedMetadata(MediaManifestEntry probed, MediaManifestEntry entry)
    {
        entry.Duration = probed.Duration;
        entry.SourceWidth = probed.SourceWidth;
        entry.SourceHeight = probed.SourceHeight;
        entry.SourceFps = probed.SourceFps;
        entry.HasAudio = probed.HasAudio;
    }

    private static string ImportFailureReason(Exception ex)
    {
        var message = (ex is AggregateException { InnerException: { } inner } ? inner : ex).Message.Trim();
        return string.IsNullOrEmpty(message) ? ex.GetType().Name : message;
    }

    private static Task<RemoteDownloadResult> DownloadImportAsync(Uri uri, string destination, CancellationToken token)
        => ImportDownloadOverride is { } download
            ? download(uri, destination, token)
            : RemoteMediaDownloader.DownloadAsync(ImportHttpClient, uri, destination, RemoteImportMaxBytes, token);

    private static async Task CopyImportAsync(string source, string destination, CancellationToken token)
    {
        const int bufferBytes = 1024 * 1024;
        // Synchronous dispose, as in RemoteMediaDownloader: an `await using` would capture a context.
        using var input = new FileStream(
            source, FileMode.Open, FileAccess.Read, FileShare.Read, bufferBytes,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var output = new FileStream(
            destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferBytes,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await input.CopyToAsync(output, bufferBytes, token).ConfigureAwait(false);
        await output.FlushAsync(token).ConfigureAwait(false);
    }

    private static Uri ValidateImportUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException($"import_media source.url must be an absolute HTTPS URL: {url}");
        if (!string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidOperationException("import_media source.url must not embed credentials");
        return uri;
    }

    /// <summary>
    /// An inline import's response: the manifest entry exactly as it was always returned, plus the
    /// Mac's <c>mediaRef</c> and <c>status: "ready"</c>, so a caller can branch on status alone.
    /// </summary>
    private static JsonObject ReadyImportResponse(MediaManifestEntry entry)
    {
        var response = JsonSerializer.SerializeToNode(entry)!.AsObject();
        response["mediaRef"] = entry.Id;
        response["status"] = "ready";
        return response;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup must not replace the outcome being reported.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best-effort cleanup must not replace the outcome being reported.
        }
    }
}
