using System.Text.Json;
using Microsoft.UI.Xaml;
using Temno.App.Services;
using Temno.Core.Project;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Temno.App.ViewModels;

public sealed class ProjectItemViewModel
{
    public ProjectItemViewModel(ProjectEntry entry)
    {
        Entry = entry;
        Path = AppSession.PathFromEntry(entry);
        Name = string.IsNullOrWhiteSpace(entry.Name)
            ? System.IO.Path.GetFileNameWithoutExtension(Path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar))
            : entry.Name;
        LastOpenedLabel = FormatRelative(entry.LastOpenedDate);
        LastOpened = entry.LastOpenedDate;
        IsAccessible = entry.IsAccessible;
        Subtitle = IsAccessible ? Path : "Missing — " + Path;
        CardOpacity = IsAccessible ? 1.0 : 0.55;
        Thumbnail = LoadThumbnail(Path, IsAccessible);
        (MetadataLabel, DurationSeconds) = IsAccessible ? ReadMetadata(Path) : ("Missing from disk", 0);
        FallbackLabel = IsAccessible ? "No preview yet" : "Missing";
        AutomationName = IsAccessible ? $"Open {Name}" : $"{Name}, missing from disk";
    }

    /// <summary>WP-186: canvas, frame rate and duration, e.g. "1920 × 1080 · 30 fps · 1:23"; empty when unknown.</summary>
    public string MetadataLabel { get; }

    /// <summary>WP-186: the poster fallback line under the glyph.</summary>
    public string FallbackLabel { get; }

    public string AutomationName { get; }

    /// <summary>WP-186: the glyph-and-line fallback shows only when there is no poster.</summary>
    public Visibility FallbackVisibility => Thumbnail is null ? Visibility.Visible : Visibility.Collapsed;

    public ProjectEntry Entry { get; }
    public string Name { get; }
    public string Path { get; }
    public string LastOpenedLabel { get; }

    /// <summary>WP-203: sort key for the "Last opened" order (the registry timestamp, not the label).</summary>
    public DateTimeOffset LastOpened { get; }

    /// <summary>WP-203: sort key for the "Duration" order — longest timeline end in seconds, 0 when unknown.</summary>
    public double DurationSeconds { get; }
    public string Subtitle { get; }
    public bool IsAccessible { get; }
    public double CardOpacity { get; }
    public ImageSource? Thumbnail { get; }

    private static ImageSource? LoadThumbnail(string packagePath, bool accessible)
    {
        if (!accessible) return null;
        var thumbnailPath = System.IO.Path.Combine(packagePath, ProjectConstants.ThumbnailFilename);
        if (!File.Exists(thumbnailPath)) return null;
        try { return new BitmapImage(new Uri(thumbnailPath)); }
        catch { return null; }
    }

    /// <summary>
    /// WP-186. Reads canvas, frame rate and the last clip end from project.json once per card. The
    /// registry format is untouched. Anything odd (legacy shape, huge file, bad JSON) yields "".
    /// WP-203: also returns the duration in seconds as the sort key (0 when unknown).
    /// </summary>
    private static (string Label, double Seconds) ReadMetadata(string packagePath)
    {
        try
        {
            var file = System.IO.Path.Combine(packagePath, ProjectConstants.TimelineFilename);
            var info = new FileInfo(file);
            if (!info.Exists || info.Length > 16L * 1024 * 1024)
                return ("", 0);

            using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
            var root = doc.RootElement;
            var timeline = root;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("timelines", out var timelines)
                && timelines.ValueKind == JsonValueKind.Array
                && timelines.GetArrayLength() > 0)
            {
                timeline = timelines[0];
                if (root.TryGetProperty("activeTimelineId", out var active) && active.ValueKind == JsonValueKind.String)
                {
                    foreach (var candidate in timelines.EnumerateArray())
                    {
                        if (candidate.TryGetProperty("id", out var id)
                            && id.ValueKind == JsonValueKind.String
                            && id.GetString() == active.GetString())
                        {
                            timeline = candidate;
                            break;
                        }
                    }
                }
            }

            var fps = ReadInt(timeline, "fps");
            var width = ReadInt(timeline, "width");
            var height = ReadInt(timeline, "height");
            long endFrame = 0;
            if (timeline.TryGetProperty("tracks", out var tracks) && tracks.ValueKind == JsonValueKind.Array)
            {
                foreach (var track in tracks.EnumerateArray())
                {
                    if (!track.TryGetProperty("clips", out var clips) || clips.ValueKind != JsonValueKind.Array)
                        continue;
                    foreach (var clip in clips.EnumerateArray())
                        endFrame = Math.Max(endFrame, ReadLong(clip, "startFrame") + ReadLong(clip, "durationFrames"));
                }
            }

            var parts = new List<string>(3);
            var seconds = 0d;
            if (width > 0 && height > 0) parts.Add($"{width} × {height}");
            if (fps > 0) parts.Add($"{fps} fps");
            if (fps > 0 && endFrame > 0)
            {
                seconds = endFrame / (double)fps;
                parts.Add(FormatDuration(seconds));
            }
            return (string.Join(" · ", parts), seconds);
        }
        catch
        {
            return ("", 0);
        }
    }

    private static int ReadInt(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt32(out var parsed)
            ? parsed
            : 0;

    private static long ReadLong(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt64(out var parsed)
            ? parsed
            : 0;

    private static string FormatDuration(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes}:{span.Seconds:00}";
    }

    private static string FormatRelative(DateTimeOffset when)
    {
        var local = when.ToLocalTime();
        var span = DateTimeOffset.Now - local;
        if (span.TotalMinutes < 1) return "Just now";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours}h ago";
        if (span.TotalDays < 7) return $"{(int)span.TotalDays}d ago";
        return local.ToString("MMM d, yyyy");
    }
}
