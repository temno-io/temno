namespace Temno.Core.Ui;

public enum PopoutSide
{
    Bottom,
    Top,
}

public enum PopoutAlignment
{
    Left,
    Center,
    Right,
}

public readonly record struct PopoutLayout(PopoutSide Side, double MaxHeight);

/// <summary>
/// Resolves popout placement and maximum presenter height against the window's bounds so menus
/// and popovers open on the expected side of their anchor and scroll inside the window instead
/// of jumping above or outside the application frame when Temno is scaled smaller.
/// </summary>
public static class PopoutPlacement
{
    /// <summary>Minimum room in the preferred direction before flipping to the opposite side.</summary>
    public const double DefaultMinPreferredHeight = 180;

    /// <summary>Floor for computed max height so presenters never collapse to zero on tiny windows.</summary>
    public const double MinPresenterHeight = 64;

    public static PopoutLayout Resolve(
        double anchorTop,
        double anchorBottom,
        double windowHeight,
        PopoutSide preferredSide,
        double margin,
        double? maxCap = null,
        double minPreferredHeight = DefaultMinPreferredHeight,
        double? topMargin = null)
    {
        if (windowHeight <= 0)
        {
            var fallback = maxCap is > 0 ? maxCap.Value : 400;
            return new PopoutLayout(preferredSide, fallback);
        }

        var safeBottomMargin = Math.Max(0, margin);
        var safeTopMargin = Math.Max(0, topMargin ?? margin);
        var clampedTop = Math.Clamp(anchorTop, 0, windowHeight);
        var clampedBottom = Math.Clamp(anchorBottom, clampedTop, windowHeight);

        var roomBelow = Math.Max(0, windowHeight - clampedBottom - safeBottomMargin);
        var roomAbove = Math.Max(0, clampedTop - safeTopMargin);

        var preferredRoom = preferredSide == PopoutSide.Bottom ? roomBelow : roomAbove;
        var oppositeRoom = preferredSide == PopoutSide.Bottom ? roomAbove : roomBelow;
        var oppositeSide = preferredSide == PopoutSide.Bottom ? PopoutSide.Top : PopoutSide.Bottom;

        var keepPreferred = preferredRoom >= minPreferredHeight || preferredRoom >= oppositeRoom;
        var chosenSide = keepPreferred ? preferredSide : oppositeSide;
        var available = keepPreferred ? preferredRoom : oppositeRoom;

        var activeMargin = chosenSide == PopoutSide.Bottom ? safeBottomMargin : safeTopMargin;
        var floor = Math.Min(MinPresenterHeight, Math.Max(0, windowHeight - activeMargin));
        var maxHeight = Math.Max(floor, available);
        if (maxCap is > 0)
            maxHeight = Math.Min(maxHeight, maxCap.Value);

        return new PopoutLayout(chosenSide, maxHeight);
    }

    public static double ContextMaxHeight(double windowHeight, double margin, double? maxCap = null)
    {
        if (windowHeight <= 0)
            return maxCap is > 0 ? maxCap.Value : 400;

        var safeMargin = Math.Max(0, margin);
        var available = Math.Max(0, windowHeight - 2 * safeMargin);
        var floor = Math.Min(MinPresenterHeight, Math.Max(0, windowHeight - safeMargin));
        var maxHeight = Math.Max(floor, available);
        if (maxCap is > 0)
            maxHeight = Math.Min(maxHeight, maxCap.Value);
        return maxHeight;
    }

    public static double PopoverWidth(double preferredWidth, double windowWidth, double margin)
    {
        if (windowWidth <= 0)
            return preferredWidth;
        var available = Math.Max(160, windowWidth - 2 * Math.Max(0, margin));
        return Math.Min(preferredWidth, available);
    }
}
