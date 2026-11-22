namespace Temno.Core.Captions;

/// <summary>
/// Column count and cell size for one caption-preset group.
/// WinUI-free so the 3-1 wrap can be locked in Core.Tests (WP-143).
/// </summary>
/// <remarks>
/// <para>
/// Preferred cell is Mac's 84×48. Gap is <c>AppTheme.Spacing.Sm</c> (6) when called from the
/// gallery; tests pass 6 explicitly so Core stays theme-free.
/// </para>
/// <para>
/// The old view did <c>(int)(width / 90)</c>. At the default Captions width (~352 px after rail
/// and <c>Padding.Lg</c>) that is 3 columns, so four cells wrap 3-1. Four preferred cells plus
/// three gaps need 354 px. This helper forces four columns from 320 px up and shrinks the cell
/// rather than wrapping.
/// </para>
/// </remarks>
public readonly record struct CaptionPresetGalleryLayout(int Columns, double CellWidth, double CellHeight)
{
    public const double PreferredCellWidth = 84;
    public const double PreferredCellHeight = 48;

    /// <summary>Audit window: four-across is required at this width and above (up to 560 px).</summary>
    public const double FourAcrossMinWidth = 320;

    public static CaptionPresetGalleryLayout Preferred { get; } =
        new(4, PreferredCellWidth, PreferredCellHeight);

    public static CaptionPresetGalleryLayout ForWidth(double width, int itemCount, double gap)
    {
        itemCount = Math.Max(1, itemCount);
        width = Math.Max(1, width);
        gap = Math.Max(0, gap);

        if (itemCount <= 4 && width + 1e-9 >= FourAcrossMinWidth)
        {
            var columns = itemCount;
            var cellW = Math.Min(PreferredCellWidth, CellWidthToFit(width, columns, gap));
            var cellH = PreferredCellHeight * (cellW / PreferredCellWidth);
            return new(columns, cellW, cellH);
        }

        var fit = FitColumns(width, itemCount, PreferredCellWidth, gap);
        return new(fit, PreferredCellWidth, PreferredCellHeight);
    }

    /// <summary>
    /// The pre-WP-143 formula. Kept so tests can prove it is what produced the 3-1 wrap.
    /// </summary>
    public static int LegacyColumns(double width, int itemCount)
    {
        itemCount = Math.Max(1, itemCount);
        var columns = Math.Max(1, (int)(Math.Max(width, PreferredCellWidth) / 90));
        return Math.Clamp(columns, 1, itemCount);
    }

    private static int FitColumns(double width, int itemCount, double cellWidth, double gap)
    {
        var stride = cellWidth + gap;
        if (stride <= 0)
            return 1;
        var fit = (int)Math.Floor((width + gap) / stride);
        return Math.Clamp(fit, 1, itemCount);
    }

    private static double CellWidthToFit(double width, int columns, double gap)
    {
        var available = width - gap * Math.Max(0, columns - 1);
        return Math.Max(1, available / Math.Max(1, columns));
    }
}
