using Temno.Core.Models;

namespace Temno.Core.Ui;

/// <summary>Human labels for selections; internal identifiers belong in diagnostic details.</summary>
public static class ClipPresentation
{
    public static string Title(Clip clip, MediaManifest? manifest)
    {
        if (!string.IsNullOrWhiteSpace(clip.TextContent))
        {
            var text = clip.TextContent.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return text.Length > 48 ? text[..48] + "…" : text;
        }
        var entry = manifest?.Entries.FirstOrDefault(e => e.Id == clip.MediaRef);
        if (!string.IsNullOrWhiteSpace(entry?.Name) && !Guid.TryParse(entry.Name, out _))
            return entry.Name;
        if (!string.IsNullOrWhiteSpace(clip.MediaRef) && !Guid.TryParse(clip.MediaRef, out _))
            return clip.MediaRef.Replace('\\', '/').Split('/').Last();
        return clip.MediaType switch
        {
            ClipType.video => "Video clip",
            ClipType.audio => "Audio clip",
            ClipType.image => "Image clip",
            ClipType.text => "Text clip",
            ClipType.subtitle => "Caption",
            _ => "Clip",
        };
    }
}
