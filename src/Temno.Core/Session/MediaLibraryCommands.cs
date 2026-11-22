using System.IO.Compression;
using System.Threading;
using Temno.Core.Models;
using Temno.Core.Project;

namespace Temno.Core.Session;

/// <summary>Mac: EditorViewModel+Folders.swift and EditorViewModel+Matte.swift.</summary>
public static class MediaLibraryCommands
{
    public const int MaxInlineBase64Length = 15 * 1024 * 1024;

    public static IReadOnlyList<string> SupportedFileExtensions { get; } = Array.AsReadOnly(
    [
        ".mp4", ".mov", ".m4v", ".webm", ".mkv", ".avi",
        ".png", ".jpg", ".jpeg", ".gif", ".tiff", ".webp", ".heic", ".heif",
        ".mp3", ".wav", ".m4a", ".aac", ".aiff", ".aif", ".aifc", ".flac",
        ".json", ".lottie",
        ".srt", ".vtt",
    ]);

    public static EditResult CreateFolder(ProjectSession session, string name, string? parentFolderId, out string? folderId)
    {
        folderId = null;
        if (string.IsNullOrWhiteSpace(name))
            return EditResult.Fail("folder name is required");
        if (!FolderExistsOrRoot(session.Manifest, parentFolderId))
            return EditResult.Fail($"parent folder not found: {parentFolderId}");

        var folder = new MediaFolder
        {
            Id = Guid.NewGuid().ToString().ToUpperInvariant(),
            Name = name.Trim(),
            ParentFolderId = parentFolderId,
        };
        session.RecordBeforeEdit();
        session.Manifest.Folders.Add(folder);
        session.MarkDirty();
        folderId = folder.Id;
        return EditResult.Success();
    }

    public static IReadOnlyList<MediaFolder> FolderPath(MediaManifest manifest, string? folderId)
    {
        var byId = manifest.Folders.ToDictionary(f => f.Id, StringComparer.Ordinal);
        var path = new List<MediaFolder>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var current = folderId;
        while (!string.IsNullOrWhiteSpace(current) && visited.Add(current) && byId.TryGetValue(current, out var folder))
        {
            path.Add(folder);
            current = folder.ParentFolderId;
        }
        path.Reverse();
        return path;
    }

    public static EditResult MoveAssets(ProjectSession session, IEnumerable<string> mediaRefs, string? folderId)
    {
        if (!FolderExistsOrRoot(session.Manifest, folderId))
            return EditResult.Fail($"destination folder not found: {folderId}");
        var ids = mediaRefs.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0) return EditResult.Fail("at least one mediaRef is required");
        var entries = ids.Select(id => session.Manifest.Entries.FirstOrDefault(e => e.Id == id)).ToList();
        if (entries.Any(e => e is null))
            return EditResult.Fail("one or more media entries were not found");

        var changes = entries.Where(e => e!.FolderId != folderId).Cast<MediaManifestEntry>().ToList();
        if (changes.Count == 0) return EditResult.Success();
        session.RecordBeforeEdit();
        foreach (var entry in changes) entry.FolderId = folderId;
        session.MarkDirty();
        return EditResult.Success();
    }

    public static EditResult MoveFolders(ProjectSession session, IEnumerable<string> folderIds, string? parentFolderId)
    {
        if (!FolderExistsOrRoot(session.Manifest, parentFolderId))
            return EditResult.Fail($"destination folder not found: {parentFolderId}");
        var ids = folderIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0) return EditResult.Fail("at least one folder id is required");
        var byId = session.Manifest.Folders.ToDictionary(f => f.Id, StringComparer.Ordinal);
        if (ids.Any(id => !byId.ContainsKey(id)))
            return EditResult.Fail("one or more folders were not found");
        if (parentFolderId is not null && ids.Any(id => parentFolderId == id || IsDescendant(byId, parentFolderId, id)))
            return EditResult.Fail("a folder cannot be moved into itself or one of its descendants");

        var changes = ids.Select(id => byId[id]).Where(f => f.ParentFolderId != parentFolderId).ToList();
        if (changes.Count == 0) return EditResult.Success();
        session.RecordBeforeEdit();
        foreach (var folder in changes) folder.ParentFolderId = parentFolderId;
        session.MarkDirty();
        return EditResult.Success();
    }

    public static EditResult ImportBase64(
        ProjectSession session,
        string base64,
        string mimeType,
        string? name,
        string? folderId,
        out MediaManifestEntry? entry)
        => ImportBase64(session, base64, mimeType, name, folderId, prepareEntry: null, out entry);

    public static EditResult ImportBase64(
        ProjectSession session,
        string base64,
        string mimeType,
        string? name,
        string? folderId,
        Action<MediaManifestEntry, string>? prepareEntry,
        out MediaManifestEntry? entry)
    {
        entry = null;
        if (base64 is null || base64.Length == 0)
            return EditResult.Fail("source.bytes must be non-empty base64");
        if (base64.Length > MaxInlineBase64Length)
            return EditResult.Fail($"source.bytes is too large (max {MaxInlineBase64Length} characters); use source.path or source.url");
        if (!TryGetMimeType(mimeType, out var extension, out var type))
            return EditResult.Fail($"unsupported source.mimeType: {mimeType}");
        if (!FolderExistsOrRoot(session.Manifest, folderId))
            return EditResult.Fail($"destination folder not found: {folderId}");
        if (!TryDecodeBase64(base64, out var bytes))
            return EditResult.Fail("source.bytes is not valid non-empty base64");

        return AddPackageOwnedFile(
            session,
            bytes,
            extension,
            type,
            name ?? "Imported asset",
            folderId,
            out entry,
            prepareEntry: prepareEntry);
    }

    public static EditResult ImportFile(
        ProjectSession session,
        string sourcePath,
        string? name,
        string? folderId,
        out MediaManifestEntry? entry)
        => ImportFile(session, sourcePath, name, folderId, prepareEntry: null, out entry);

    /// <summary>
    /// <see cref="DestinationPath"/> is inside the package for a copied import and is the user's own
    /// file for a referenced one, which is why <see cref="IsReference"/> exists: the failure paths
    /// delete that path, and deleting the user's source would be data loss (WP-160).
    /// </summary>
    /// <param name="Grant">
    /// WP-399: for a referenced import, the grant for the user's file, resolved while preparing (off the UI
    /// thread) so the commit only records it.
    /// </param>
    public readonly record struct PreparedMediaImport(
        string DestinationPath,
        MediaManifestEntry Entry,
        bool IsReference = false,
        IO.ExternalMediaGrant? Grant = null);

    public static EditResult ImportFile(
        ProjectSession session,
        string sourcePath,
        string? name,
        string? folderId,
        Action<MediaManifestEntry, string>? prepareEntry,
        out MediaManifestEntry? entry)
    {
        entry = null;
        var preparedResult = PrepareImportedFile(
            session.PackagePath,
            session.ManifestUnreadable,
            FolderExistsOrRoot(session.Manifest, folderId),
            sourcePath,
            name,
            folderId,
            prepareEntry,
            out var prepared);
        if (!preparedResult.Ok || prepared is null)
            return preparedResult;
        var commit = CommitImportedFile(session, prepared.Value);
        if (!commit.Ok)
            return commit;
        entry = prepared.Value.Entry;
        return EditResult.Success();
    }

    /// <summary>Copy + optional probe. Does not mutate the session.</summary>
    public static EditResult PrepareImportedFile(
        string? packagePath,
        bool manifestUnreadable,
        bool folderExists,
        string sourcePath,
        string? name,
        string? folderId,
        Action<MediaManifestEntry, string>? prepareEntry,
        out PreparedMediaImport? prepared,
        bool referenceInPlace = false,
        CancellationToken cancellationToken = default)
    {
        prepared = null;
        if (manifestUnreadable)
            return EditResult.Fail("media manifest is unreadable; save a compatible project before adding media");
        if (string.IsNullOrWhiteSpace(packagePath))
            return EditResult.Fail("open a project package before importing media");
        if (string.IsNullOrWhiteSpace(sourcePath))
            return EditResult.Fail("source path is required");
        if (!folderExists)
            return EditResult.Fail($"destination folder not found: {folderId}");

        string absoluteSource;
        try
        {
            absoluteSource = Path.GetFullPath(sourcePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return EditResult.Fail($"invalid source path: {ex.Message}");
        }

        if (!File.Exists(absoluteSource))
            return EditResult.Fail($"file not found: {sourcePath}");

        var extension = Path.GetExtension(absoluteSource).ToLowerInvariant();
        if (!TryGetTypeFromExtension(extension, out var type))
            return EditResult.Fail($"unsupported file extension '{extension}'");

        var id = Guid.NewGuid().ToString().ToUpperInvariant();

        // WP-160: reference-in-place writes no bytes. Nothing is copied, so nothing is cleaned up on
        // failure either — the "destination" is the user's own file and deleting it would be a
        // catastrophe dressed as tidy-up. PreparedMediaImport.IsReference is what stops that.
        if (referenceInPlace)
        {
            try
            {
                var referenced = CreateExternalEntry(
                    id,
                    name ?? Path.GetFileNameWithoutExtension(absoluteSource),
                    type,
                    absoluteSource,
                    folderId);
                prepareEntry?.Invoke(referenced, absoluteSource);
                prepared = new PreparedMediaImport(absoluteSource, referenced, IsReference: true,
                    Grant: IO.ExternalMediaAccess.CreateGrant(absoluteSource));
                return EditResult.Success();
            }
            catch (Exception ex)
            {
                return EditResult.Fail($"failed to reference media: {ex.Message}");
            }
        }

        var packageFull = Path.GetFullPath(packagePath);
        var mediaDirectory = Path.Combine(packageFull, ProjectConstants.MediaDirectoryName);
        Directory.CreateDirectory(mediaDirectory);
        var filename = $"imported-{id[..8].ToLowerInvariant()}{extension}";
        var destination = Path.Combine(mediaDirectory, filename);
        var relativePath = $"{ProjectConstants.MediaDirectoryName}/{filename}";

        try
        {
            CopyAtomically(absoluteSource, destination, cancellationToken);
            var created = CreatePackageEntry(
                id,
                name ?? Path.GetFileNameWithoutExtension(absoluteSource),
                type,
                relativePath,
                folderId);
            prepareEntry?.Invoke(created, destination);
            prepared = new PreparedMediaImport(destination, created);
            return EditResult.Success();
        }
        catch (Exception ex)
        {
            if (File.Exists(destination)) File.Delete(destination);
            return ex is OperationCanceledException
                ? EditResult.Fail("import cancelled")
                : EditResult.Fail($"failed to copy package media: {ex.Message}");
        }
    }

    public static EditResult CommitImportedFile(ProjectSession session, PreparedMediaImport prepared)
    {
        // A referenced import owns no bytes in the package. DestinationPath is then the user's own
        // file, and the rollback below must not touch it (WP-160).
        void RollBackCopiedBytes()
        {
            if (prepared.IsReference) return;
            if (File.Exists(prepared.DestinationPath)) File.Delete(prepared.DestinationPath);
        }

        if (session.ManifestUnreadable)
        {
            RollBackCopiedBytes();
            return EditResult.Fail("media manifest is unreadable; save a compatible project before adding media");
        }
        if (!FolderExistsOrRoot(session.Manifest, prepared.Entry.FolderId))
        {
            RollBackCopiedBytes();
            return EditResult.Fail($"destination folder not found: {prepared.Entry.FolderId}");
        }

        // WP-399: the user (or the MCP caller) chose this file, so the project may use it. Recorded
        // before the entry lands so the first refresh already resolves it.
        if (prepared.IsReference)
            session.ExternalMedia.AllowPrepared(
                [prepared.Grant ?? IO.ExternalMediaAccess.CreateGrant(prepared.DestinationPath)]);

        session.RecordBeforeEdit();
        session.Manifest.Entries.Add(prepared.Entry);
        session.MarkDirty();
        return EditResult.Success();
    }

    public static EditResult CreateMatte(
        ProjectSession session,
        string hex,
        string? aspectRatio,
        string? name,
        string? folderId,
        out MediaManifestEntry? entry)
    {
        entry = null;
        if (!MatteAspectExtensions.TryParse(aspectRatio, out var aspect))
            return EditResult.Fail($"unknown matte aspectRatio: {aspectRatio}");
        if (!FolderExistsOrRoot(session.Manifest, folderId))
            return EditResult.Fail($"destination folder not found: {folderId}");
        if (!SolidMattePng.TryCreate(hex, MatteAspectExtensions.PixelSize(aspect, session.ActiveTimeline.Width, session.ActiveTimeline.Height), out var png))
            return EditResult.Fail("source.matte.hex must be a CSS-style RGB hex color");

        var size = MatteAspectExtensions.PixelSize(aspect, session.ActiveTimeline.Width, session.ActiveTimeline.Height);
        var defaultName = aspect == MatteAspect.Project
            ? $"Matte · {size.Width}×{size.Height}"
            : $"Matte · {MatteAspectExtensions.ToWire(aspect)}";
        var result = AddPackageOwnedFile(session, png, "png", ClipType.image, name ?? defaultName, folderId, out entry, "matte");
        if (result.Ok && entry is not null)
        {
            entry.SourceWidth = size.Width;
            entry.SourceHeight = size.Height;
            entry.HasAudio = false;
        }
        return result;
    }

    public static bool TryGetMimeType(string? mimeType, out string extension, out ClipType type)
    {
        extension = "";
        type = ClipType.video;
        switch (mimeType?.Trim().ToLowerInvariant())
        {
            case "video/mp4" or "video/mpeg4": extension = "mp4"; type = ClipType.video; return true;
            case "video/quicktime": extension = "mov"; type = ClipType.video; return true;
            case "audio/mpeg" or "audio/mp3": extension = "mp3"; type = ClipType.audio; return true;
            case "audio/wav" or "audio/x-wav" or "audio/wave": extension = "wav"; type = ClipType.audio; return true;
            case "audio/aac": extension = "aac"; type = ClipType.audio; return true;
            case "audio/mp4" or "audio/m4a" or "audio/x-m4a": extension = "m4a"; type = ClipType.audio; return true;
            case "audio/aiff" or "audio/x-aiff": extension = "aiff"; type = ClipType.audio; return true;
            case "audio/aifc" or "audio/x-aifc": extension = "aifc"; type = ClipType.audio; return true;
            case "audio/flac" or "audio/x-flac": extension = "flac"; type = ClipType.audio; return true;
            case "image/png": extension = "png"; type = ClipType.image; return true;
            case "image/jpeg" or "image/jpg": extension = "jpg"; type = ClipType.image; return true;
            case "image/tiff": extension = "tiff"; type = ClipType.image; return true;
            case "image/webp": extension = "webp"; type = ClipType.image; return true;
            case "image/heic" or "image/heif": extension = "heic"; type = ClipType.image; return true;
            case "application/json" or "application/vnd.lottie+json": extension = "json"; type = ClipType.lottie; return true;
            case "application/zip" or "application/vnd.lottie+zip": extension = "lottie"; type = ClipType.lottie; return true;
            case "application/x-subrip" or "application/x-subtitle" or "text/srt" or "text/x-srt": extension = "srt"; type = ClipType.subtitle; return true;
            case "text/vtt" or "text/webvtt": extension = "vtt"; type = ClipType.subtitle; return true;
            default: return false;
        }
    }

    public static bool TryGetTypeFromExtension(string? extension, out ClipType type)
    {
        type = ClipType.video;
        return extension?.Trim().ToLowerInvariant() switch
        {
            ".mp4" or ".mov" or ".m4v" or ".webm" or ".mkv" or ".avi" => true,
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".tiff" or ".webp" or ".heic" or ".heif"
                => SetType(ClipType.image, out type),
            ".mp3" or ".wav" or ".m4a" or ".aac" or ".aiff" or ".aif" or ".aifc" or ".flac"
                => SetType(ClipType.audio, out type),
            ".json" or ".lottie" => SetType(ClipType.lottie, out type),
            ".srt" or ".vtt" => SetType(ClipType.subtitle, out type),
            _ => false,
        };
    }

    private static EditResult AddPackageOwnedFile(
        ProjectSession session,
        byte[] bytes,
        string extension,
        ClipType type,
        string name,
        string? folderId,
        out MediaManifestEntry? entry,
        string filenamePrefix = "imported",
        Action<MediaManifestEntry, string>? prepareEntry = null)
    {
        entry = null;
        if (session.ManifestUnreadable)
            return EditResult.Fail("media manifest is unreadable; save a compatible project before adding media");
        if (string.IsNullOrWhiteSpace(session.PackagePath))
            return EditResult.Fail("open a project package before importing media");

        var packagePath = Path.GetFullPath(session.PackagePath);
        var mediaDirectory = Path.Combine(packagePath, ProjectConstants.MediaDirectoryName);
        Directory.CreateDirectory(mediaDirectory);
        var id = Guid.NewGuid().ToString().ToUpperInvariant();
        var filename = $"{filenamePrefix}-{id[..8].ToLowerInvariant()}.{extension}";
        var destination = Path.Combine(mediaDirectory, filename);
        var relativePath = $"{ProjectConstants.MediaDirectoryName}/{filename}";

        try
        {
            WriteAtomically(destination, bytes);
            var created = CreatePackageEntry(id, name, type, relativePath, folderId);
            prepareEntry?.Invoke(created, destination);
            session.RecordBeforeEdit();
            session.Manifest.Entries.Add(created);
            session.MarkDirty();
            entry = created;
            return EditResult.Success();
        }
        catch (Exception ex)
        {
            if (File.Exists(destination)) File.Delete(destination);
            return EditResult.Fail($"failed to write package media: {ex.Message}");
        }
    }

    public static bool FolderExistsOrRoot(MediaManifest manifest, string? folderId)
        => folderId is null || manifest.Folders.Any(folder => folder.Id == folderId);

    private static bool IsDescendant(IReadOnlyDictionary<string, MediaFolder> byId, string candidateId, string ancestorId)
    {
        var current = candidateId;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (visited.Add(current) && byId.TryGetValue(current, out var folder))
        {
            if (current == ancestorId) return true;
            if (folder.ParentFolderId is null) return false;
            current = folder.ParentFolderId;
        }
        return false;
    }

    private static bool TryDecodeBase64(string base64, out byte[] bytes)
    {
        try
        {
            bytes = Convert.FromBase64String(base64);
            return bytes.Length > 0;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }

    private static void WriteAtomically(string destination, byte[] bytes)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, destination, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>Buffer for the streamed copy. Large enough that a 26 GB file is not syscall-bound.</summary>
    private const int CopyBufferBytes = 1 << 20;

    /// <summary>
    /// WP-160: streamed rather than <c>File.Copy</c> so a large import can be cancelled. The owner's
    /// 25.9 GB source took minutes inside one uninterruptible call with no way to stop it. The temp
    /// name was already here, so a cancelled copy leaves nothing behind.
    /// </summary>
    private static void CopyAtomically(
        string source,
        string destination,
        CancellationToken cancellationToken = default)
    {
        var temporary = Path.Combine(
            Path.GetDirectoryName(destination)!,
            $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var input = new FileStream(
                       source, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferBytes))
            using (var output = new FileStream(
                       temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBufferBytes))
            {
                var buffer = new byte[CopyBufferBytes];
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, read);
                }
            }

            File.Move(temporary, destination, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static MediaManifestEntry CreatePackageEntry(
        string id,
        string name,
        ClipType type,
        string relativePath,
        string? folderId)
        => new()
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(name) ? "Imported asset" : name.Trim(),
            Type = type,
            Source = MediaSource.Project(relativePath),
            Duration = type == ClipType.image ? 0 : 10,
            // WP-405: no guessed picture size. The import's probe (prepareEntry) fills it; a guess
            // of 1920x1080 read as a canvas match and placed portrait media at full-canvas size.
            SourceWidth = null,
            SourceHeight = null,
            SourceFps = type == ClipType.video ? 30 : null,
            HasAudio = type is ClipType.audio or ClipType.video,
            FolderId = folderId,
        };

    /// <summary>
    /// WP-160: an entry that points at the file where it already lives. The on-disk format has always
    /// had this shape — <c>{"external": {"absolutePath": "…"}}</c>, read by <see cref="IO.MediaResolver"/>
    /// — but nothing in the product ever wrote one, so a 25.9 GB source could only be imported by
    /// duplicating it into the package.
    /// </summary>
    private static MediaManifestEntry CreateExternalEntry(
        string id,
        string name,
        ClipType type,
        string absolutePath,
        string? folderId)
        => new()
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(name) ? "Imported asset" : name.Trim(),
            Type = type,
            Source = MediaSource.External(absolutePath),
            Duration = type == ClipType.image ? 0 : 10,
            // WP-405: no guessed picture size. The import's probe (prepareEntry) fills it; a guess
            // of 1920x1080 read as a canvas match and placed portrait media at full-canvas size.
            SourceWidth = null,
            SourceHeight = null,
            SourceFps = type == ClipType.video ? 30 : null,
            HasAudio = type is ClipType.audio or ClipType.video,
            FolderId = folderId,
        };

    private static bool SetType(ClipType value, out ClipType type)
    {
        type = value;
        return true;
    }
}

public enum MatteAspect
{
    Project,
    SixteenNine,
    NineSixteen,
    OneOne,
    FourThree,
    NineFourteen,
    TwoPointFourOne,
}

public static class MatteAspectExtensions
{
    public static bool TryParse(string? raw, out MatteAspect aspect)
    {
        aspect = MatteAspect.Project;
        return raw?.Trim() switch
        {
            null or "" => true,
            var value when value.Equals("Project", StringComparison.OrdinalIgnoreCase) => true,
            "16:9" => Set(MatteAspect.SixteenNine, out aspect),
            "9:16" => Set(MatteAspect.NineSixteen, out aspect),
            "1:1" => Set(MatteAspect.OneOne, out aspect),
            "4:3" => Set(MatteAspect.FourThree, out aspect),
            "9:14" => Set(MatteAspect.NineFourteen, out aspect),
            "2.4:1" => Set(MatteAspect.TwoPointFourOne, out aspect),
            _ => false,
        };
    }

    public static (int Width, int Height) PixelSize(MatteAspect aspect, int timelineWidth, int timelineHeight)
    {
        var ratio = aspect switch
        {
            MatteAspect.SixteenNine => (16, 9),
            MatteAspect.NineSixteen => (9, 16),
            MatteAspect.OneOne => (1, 1),
            MatteAspect.FourThree => (4, 3),
            MatteAspect.NineFourteen => (9, 14),
            MatteAspect.TwoPointFourOne => (24, 10),
            _ => (0, 0),
        };
        if (ratio == (0, 0)) return Even(timelineWidth, timelineHeight);
        var shortEdge = Math.Max(2, Math.Min(timelineWidth, timelineHeight));
        return ratio.Item1 >= ratio.Item2
            ? Even((int)Math.Round((double)shortEdge * ratio.Item1 / ratio.Item2), shortEdge)
            : Even(shortEdge, (int)Math.Round((double)shortEdge * ratio.Item2 / ratio.Item1));
    }

    public static string ToWire(MatteAspect aspect) => aspect switch
    {
        MatteAspect.SixteenNine => "16:9",
        MatteAspect.NineSixteen => "9:16",
        MatteAspect.OneOne => "1:1",
        MatteAspect.FourThree => "4:3",
        MatteAspect.NineFourteen => "9:14",
        MatteAspect.TwoPointFourOne => "2.4:1",
        _ => "Project",
    };

    private static (int Width, int Height) Even(int width, int height)
        => (Math.Max(2, Math.Max(2, width) / 2 * 2), Math.Max(2, Math.Max(2, height) / 2 * 2));

    private static bool Set(MatteAspect value, out MatteAspect aspect)
    {
        aspect = value;
        return true;
    }
}

internal static class SolidMattePng
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static bool TryCreate(string? hex, (int Width, int Height) size, out byte[] png)
    {
        png = [];
        if (!TryParseColor(hex, out var color)) return false;
        using var output = new MemoryStream();
        output.Write(Signature);
        Span<byte> header = stackalloc byte[13];
        WriteUInt32(header, 0, (uint)size.Width);
        WriteUInt32(header, 4, (uint)size.Height);
        header[8] = 8;
        header[9] = 6; // RGBA
        WriteChunk(output, "IHDR", header);

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            var row = new byte[1 + size.Width * 4];
            for (var x = 0; x < size.Width; x++)
            {
                row[1 + x * 4] = color.R;
                row[2 + x * 4] = color.G;
                row[3 + x * 4] = color.B;
                row[4 + x * 4] = 255;
            }
            for (var y = 0; y < size.Height; y++) zlib.Write(row);
        }
        WriteChunk(output, "IDAT", compressed.ToArray());
        WriteChunk(output, "IEND", []);
        png = output.ToArray();
        return true;
    }

    private static bool TryParseColor(string? hex, out (byte R, byte G, byte B) color)
    {
        color = default;
        var raw = hex?.Trim().TrimStart('#');
        if (raw is null) return false;
        if (raw.Length == 3)
            raw = string.Concat(raw.Select(c => new string(c, 2)));
        if (raw.Length != 6 || !uint.TryParse(raw, System.Globalization.NumberStyles.HexNumber, null, out var rgb))
            return false;
        color = ((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }

    private static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        WriteUInt32(length, 0, (uint)data.Length);
        output.Write(length);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);
        var crc = Crc32(typeBytes, data);
        Span<byte> crcBytes = stackalloc byte[4];
        WriteUInt32(crcBytes, 0, crc);
        output.Write(crcBytes);
    }

    private static void WriteUInt32(Span<byte> bytes, int offset, uint value)
    {
        bytes[offset] = (byte)(value >> 24);
        bytes[offset + 1] = (byte)(value >> 16);
        bytes[offset + 2] = (byte)(value >> 8);
        bytes[offset + 3] = (byte)value;
    }

    private static uint Crc32(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in first)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xEDB88320u);
        }
        foreach (var value in second)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xEDB88320u);
        }
        return ~crc;
    }
}
