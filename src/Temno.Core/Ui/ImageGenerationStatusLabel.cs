namespace Temno.Core.Ui;

public static class ImageGenerationStatusLabel
{
    /// <param name="blocked">
    /// WP-399: a linked file this project may not use until the user allows it.
    /// </param>
    public static string Format(string kind, bool linked, string? operation, string? status, bool blocked = false)
    {
        var ordinary = blocked ? $"{kind} · linked · not allowed" : linked ? $"{kind} · linked" : kind;
        // import_media: the view model passes it for a queued import placeholder (WP-169).
        if (operation is not ("generate_image" or "edit_image" or "import_media")) return ordinary;
        return status switch
        {
            "preparing" or "generating" => "Generating…",
            "downloading" => "Downloading…",
            { } failure when failure.StartsWith("failed:", StringComparison.Ordinal)
                => "Failed: " + failure[7..].Trim(),
            _ => ordinary,
        };
    }
}
