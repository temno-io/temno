namespace Temno.Media;

/// <summary>
/// Preview raster size: even dimensions that fit inside a max edge so decode stays watchable.
/// </summary>
public static class PreviewRaster
{
    /// <summary>Longest edge for sequential preview decode. 2560 keeps 1440p native; 4K still fits.</summary>
    public const int DefaultMaxDimension = 2560;

    /// <summary>
    /// Longest edge for interactive preview decode and the software composite canvas (WP-163).
    /// 1280 quarters pixel work (decode preroll, composite, BGRA present) versus
    /// <see cref="DefaultMaxDimension"/> while staying above the ~880 px on-screen preview,
    /// so the downscale in XAML hides it. Export never uses this: <c>ExportFrameSource</c>
    /// decodes at output size.
    /// </summary>
    public const int PreviewMaxDimension = 1280;

    public static (int Width, int Height) Fit(int sourceWidth, int sourceHeight, int maxDimension = DefaultMaxDimension)
    {
        if (sourceWidth <= 0) throw new ArgumentOutOfRangeException(nameof(sourceWidth));
        if (sourceHeight <= 0) throw new ArgumentOutOfRangeException(nameof(sourceHeight));
        if (maxDimension <= 0) throw new ArgumentOutOfRangeException(nameof(maxDimension));

        if (sourceWidth <= maxDimension && sourceHeight <= maxDimension)
            return (Even(sourceWidth), Even(sourceHeight));

        var scale = maxDimension / (double)Math.Max(sourceWidth, sourceHeight);
        return (
            Even(Math.Max(2, (int)Math.Round(sourceWidth * scale))),
            Even(Math.Max(2, (int)Math.Round(sourceHeight * scale))));
    }

    private static int Even(int value) => Math.Max(2, value & ~1);
}
