using Temno.Core.Editing;
using Temno.Core.Models;

namespace Temno.Core.Session;

public static partial class EditCommands
{
    public static EditResult RenameMediaEntry(ProjectSession session, string mediaRef, string newName)
    {
        if (string.IsNullOrWhiteSpace(mediaRef))
            return EditResult.Fail("mediaRef is required");
        if (string.IsNullOrWhiteSpace(newName))
            return EditResult.Fail("newName is required");

        var entry = session.Manifest.Entries.FirstOrDefault(e => e.Id == mediaRef);
        if (entry is null)
            return EditResult.Fail($"media not found: {mediaRef}");
        if (string.Equals(entry.Name, newName, StringComparison.Ordinal))
            return EditResult.Success();

        return ExecuteWithUndo(session, () =>
        {
            var target = session.Manifest.Entries.FirstOrDefault(e => e.Id == mediaRef);
            if (target is null)
                return EditResult.Fail($"media not found: {mediaRef}");
            target.Name = newName.Trim();
            session.MarkDirty();
            return EditResult.Success();
        });
    }

    /// <summary>
    /// Removes a media entry from the manifest. Fails when any clip still references it
    /// unless <paramref name="force"/> is true (then referencing clips are left dangling).
    /// </summary>
    public static EditResult RemoveMediaEntry(ProjectSession session, string mediaRef, bool force = false)
    {
        if (string.IsNullOrWhiteSpace(mediaRef))
            return EditResult.Fail("mediaRef is required");
        if (session.Manifest.Entries.All(e => e.Id != mediaRef))
            return EditResult.Fail($"media not found: {mediaRef}");

        if (!force)
        {
            var inUse = session.ProjectFile.Timelines
                .SelectMany(t => t.Tracks)
                .SelectMany(tr => tr.Clips)
                .Any(c => string.Equals(c.MediaRef, mediaRef, StringComparison.Ordinal));
            if (inUse)
                return EditResult.Fail("media is used by one or more clips; remove those clips first or pass force");
        }

        return ExecuteWithUndo(session, () =>
        {
            session.Manifest.Entries.RemoveAll(e => e.Id == mediaRef);
            session.MarkDirty();
            return EditResult.Success();
        });
    }

    /// <summary>Points a media entry at a new absolute path (offline relink).</summary>
    public static EditResult RelinkMediaEntry(ProjectSession session, string mediaRef, string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(mediaRef))
            return EditResult.Fail("mediaRef is required");
        if (string.IsNullOrWhiteSpace(absolutePath))
            return EditResult.Fail("absolutePath is required");

        var entry = session.Manifest.Entries.FirstOrDefault(e => e.Id == mediaRef);
        if (entry is null)
            return EditResult.Fail($"media not found: {mediaRef}");
        if (MediaLibraryCommands.TryGetTypeFromExtension(Path.GetExtension(absolutePath), out var candidateType)
            && candidateType != entry.Type)
        {
            return EditResult.Fail($"cannot relink {entry.Type} media to {candidateType} media");
        }

        return ExecuteWithUndo(session, () =>
        {
            var target = session.Manifest.Entries.FirstOrDefault(e => e.Id == mediaRef);
            if (target is null)
                return EditResult.Fail($"media not found: {mediaRef}");
            // WP-399: the user picked this file, so the project may use it.
            session.ExternalMedia.Allow(absolutePath);
            target.Source = MediaSource.External(absolutePath);
            if (string.IsNullOrWhiteSpace(target.Name) || target.Name == mediaRef)
                target.Name = System.IO.Path.GetFileName(absolutePath);
            session.MarkDirty();
            return EditResult.Success();
        });
    }

    public static EditResult SetProjectSettings(
        ProjectSession session,
        int? fps,
        int? width,
        int? height,
        string? aspectRatio,
        string? quality)
    {
        return ExecuteWithUndo(session, () =>
        {
            var timeline = session.ActiveTimeline;
            var newFps = fps ?? timeline.Fps;
            if (newFps < 1 || newFps > 120)
                return EditResult.Fail("Frame rate must be between 1 and 120 fps.");
            var newWidth = timeline.Width;
            var newHeight = timeline.Height;

            if (aspectRatio != null)
            {
                switch (aspectRatio)
                {
                    case "16:9": newWidth = 1920; newHeight = 1080; break;
                    case "9:14": newWidth = 1080; newHeight = 1680; break;
                    case "9:16": newWidth = 1080; newHeight = 1920; break;
                    case "1:1": newWidth = 1080; newHeight = 1080; break;
                    case "4:3": newWidth = 1440; newHeight = 1080; break;
                    case "2.4:1": newWidth = 2560; newHeight = 1080; break;
                }
            }

            if (quality != null)
            {
                int shortEdge = quality switch
                {
                    "720p" => 720,
                    "1080p" => 1080,
                    "2K" => 1440,
                    "4K" => 2160,
                    _ => 1080
                };
                static int RoundEven(double v) => Math.Max(2, (int)Math.Round(v / 2.0) * 2);
                if (newWidth <= newHeight)
                {
                    newHeight = RoundEven((double)shortEdge * newHeight / Math.Max(1, newWidth));
                    newWidth = shortEdge;
                }
                else
                {
                    newWidth = RoundEven((double)shortEdge * newWidth / Math.Max(1, newHeight));
                    newHeight = shortEdge;
                }
            }
            else
            {
                newWidth = width ?? newWidth;
                newHeight = height ?? newHeight;
            }
            if (newWidth < 2 || newHeight < 2)
                return EditResult.Fail("Canvas width and height must be at least 2 px.");

            // WP-405 + project-wide canvas settings: canvas resolution and frame rate apply across
            // every timeline in the project. Refit all timelines before mutating dimensions so
            // nested sequence lookups read consistent pre-change source dimensions.
            var timelines = session.ProjectFile.Timelines;
            foreach (var tl in timelines)
            {
                if (tl.Width != newWidth || tl.Height != newHeight)
                {
                    ClipRefit.ForCanvasChange(tl, newWidth, newHeight,
                        clip => ClipRefit.SourceSize(session.Manifest, timelines, clip));
                }
            }

            foreach (var tl in timelines)
            {
                tl.Fps = newFps;
                tl.Width = newWidth;
                tl.Height = newHeight;
                tl.SettingsConfigured = true;
            }

            session.MarkDirty();
            return EditResult.Success();
        });
    }
}
