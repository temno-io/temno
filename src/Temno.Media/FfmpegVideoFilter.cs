namespace Temno.Media;

/// <summary>
/// ffmpeg <c>-vf</c> snippets for preview decode. Identity sizes skip <c>scale</c> so 1080p
/// is not bilinear-softened; real resizes use lanczos (Mac Core Image interpolates).
/// </summary>
public static class FfmpegVideoFilter
{
    public const string ScaleFlags = "lanczos";

    public static string BgraConvert(int destWidth, int destHeight, int sourceWidth, int sourceHeight)
        => Convert(destWidth, destHeight, sourceWidth, sourceHeight, "bgra");

    /// <summary>
    /// Converts to <paramref name="pixelFormat"/>. Preview wants BGRA because that is what
    /// WriteableBitmap blits; export wants RGBA because that is the compositor canvas and the
    /// encoder pipe format — matching it there lets a full-frame clip skip compositing entirely.
    /// </summary>
    public static string Convert(
        int destWidth, int destHeight, int sourceWidth, int sourceHeight, string pixelFormat)
    {
        if (destWidth <= 0) throw new ArgumentOutOfRangeException(nameof(destWidth));
        if (destHeight <= 0) throw new ArgumentOutOfRangeException(nameof(destHeight));
        ArgumentException.ThrowIfNullOrWhiteSpace(pixelFormat);
        if (sourceWidth > 0 && sourceHeight > 0 && destWidth == sourceWidth && destHeight == sourceHeight)
            return $"format={pixelFormat}";
        return $"scale={destWidth}:{destHeight}:flags={ScaleFlags},format={pixelFormat}";
    }
}
