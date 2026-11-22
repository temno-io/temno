namespace Temno.Core.Captions;

/// <summary>
/// Captions-tab surface conversions. The model stays in seconds and normalised centres;
/// only the panel talks milliseconds and canvas pixels (WP-128).
/// </summary>
public static class CaptionFormLogic
{
    /// <summary>UI default for max words. <see cref="CaptionRequest.MaxWords"/> itself defaults to unlimited.</summary>
    public const int DefaultMaxWords = 4;

    public const int MaxWordsLimit = 50;

    public const int MaxCharactersLimit = 200;

    public const double GapMsDefault = 500;

    public const double GapMsMaximum = 2000;

    public const double DefaultFontSize = 48;

    public const double DefaultCenterX = 0.5;

    public const double DefaultCenterY = 0.9;

    public const string DefaultFontName = "Helvetica-Bold";

    /// <summary>
    /// Milliseconds → seconds at the <see cref="CaptionRequest.MaximumGapSeconds"/> boundary.
    /// 500 ms is the same generate input as the old 0.5 s slider.
    /// </summary>
    public static double GapMsToSeconds(double milliseconds)
        => CaptionSpecBuilder.ClampGapSeconds(milliseconds / 1000.0);

    public static double GapSecondsToMs(double seconds)
        => Math.Round(CaptionSpecBuilder.ClampGapSeconds(seconds) * 1000.0, MidpointRounding.AwayFromZero);

    /// <summary>0 (and anything that rounds to 0) means unlimited, matching the previous NumberBoxes.</summary>
    public static int? UnlimitedOrCount(double value)
    {
        var rounded = (int)Math.Round(double.IsNaN(value) ? 0 : value, MidpointRounding.AwayFromZero);
        return rounded >= 1 ? rounded : null;
    }

    public static double PixelFromNormalized(double normalized, double canvas)
        => Math.Round(Math.Clamp(normalized, 0, 1) * Math.Max(1, canvas), MidpointRounding.AwayFromZero);

    public static double NormalizedFromPixel(double pixels, double canvas)
        => canvas <= 0 ? 0 : Math.Clamp(pixels / canvas, 0, 1);
}
