using Temno.Core.Models;

namespace Temno.Core.Editing;

/// <summary>
/// WP-405: where a clip's picture lands inside its transform. The transform's centre, width and
/// height (canvas fractions) are the user's box; the picture keeps its own aspect inside that box,
/// centred, the way Final Cut's Fit and Resolve's "scale entire image to fit" place media. The Mac
/// app stretches the picture to the box instead. Mac-placed clips draw the same either way, because
/// <see cref="LayoutEngine.FitTransform(double, double, int, int)"/> gives every clip a box of its
/// source's own aspect; only a box of another shape (a clip placed before its dimensions were known,
/// a canvas changed without a re-fit, a hand-written transform) draws differently: fitted, not stretched.
/// </summary>
public static class ClipPlacement
{
    /// <summary>
    /// Mac <c>Defaults.aspectTolerance</c>, the tolerance <see cref="LayoutEngine.FitTransform(double, double, int, int)"/>
    /// already uses: a box within this of the picture's aspect draws as the box itself, so a source a
    /// pixel off the canvas shape still fills it exactly.
    /// </summary>
    public const double AspectTolerance = 0.02;

    /// <summary>
    /// The rectangle a <paramref name="pictureWidth"/>×<paramref name="pictureHeight"/> picture
    /// actually covers inside <paramref name="box"/> on a canvas of the given pixel size: the largest
    /// rectangle of the picture's aspect that fits the box, at the box's centre, with the box's
    /// rotation and flips. Returns <paramref name="box"/> itself when the shapes already agree, or when
    /// any size is unknown or empty.
    /// </summary>
    public static Transform FitInBox(Transform box, double pictureWidth, double pictureHeight, int canvasWidth, int canvasHeight)
    {
        ArgumentNullException.ThrowIfNull(box);
        if (!(pictureWidth > 0) || !(pictureHeight > 0) || canvasWidth <= 0 || canvasHeight <= 0)
            return box;

        var boxWidth = box.Width * canvasWidth;
        var boxHeight = box.Height * canvasHeight;
        if (!(boxWidth > 0) || !(boxHeight > 0)) return box;

        var pictureAspect = pictureWidth / pictureHeight;
        var boxAspect = boxWidth / boxHeight;
        if (Math.Abs(boxAspect - pictureAspect) < AspectTolerance) return box;

        var fitted = box.Clone();
        if (pictureAspect > boxAspect)
            fitted.Height = boxWidth / pictureAspect / canvasHeight;
        else
            fitted.Width = boxHeight * pictureAspect / canvasWidth;
        return fitted;
    }

    /// <summary>
    /// Mac <c>FrameRenderer.applyClipPipeline</c>: crop trims the picture to its visible region, and
    /// that region draws exactly where it sat in the uncropped picture. <paramref name="placed"/> is
    /// the whole picture's placement (already through <see cref="FitInBox"/>). Returns the visible
    /// pixel rectangle and the placement of just that rectangle, or null when the crop leaves nothing.
    /// </summary>
    public static CropRegion? VisibleRegion(Transform placed, int pictureWidth, int pictureHeight, Crop crop, int canvasWidth, int canvasHeight)
    {
        ArgumentNullException.ThrowIfNull(placed);
        ArgumentNullException.ThrowIfNull(crop);
        if (pictureWidth <= 0 || pictureHeight <= 0 || canvasWidth <= 0 || canvasHeight <= 0) return null;

        var x0 = Math.Clamp((int)Math.Round(crop.Left * pictureWidth), 0, pictureWidth);
        var x1 = Math.Clamp(pictureWidth - (int)Math.Round(crop.Right * pictureWidth), 0, pictureWidth);
        var y0 = Math.Clamp((int)Math.Round(crop.Top * pictureHeight), 0, pictureHeight);
        var y1 = Math.Clamp(pictureHeight - (int)Math.Round(crop.Bottom * pictureHeight), 0, pictureHeight);
        if (x1 - x0 < 1 || y1 - y0 < 1) return null;

        var u0 = x0 / (double)pictureWidth;
        var u1 = x1 / (double)pictureWidth;
        var v0 = y0 / (double)pictureHeight;
        var v1 = y1 / (double)pictureHeight;

        // The region's centre, relative to the picture's centre, in canvas pixels before rotation. A
        // flip mirrors where the region sits as well as what it shows.
        var dx = ((u0 + u1) / 2 - 0.5) * placed.Width * canvasWidth * (placed.FlipHorizontal ? -1 : 1);
        var dy = ((v0 + v1) / 2 - 0.5) * placed.Height * canvasHeight * (placed.FlipVertical ? -1 : 1);

        // The compositors rotate about the placement's centre, clockwise on screen for a positive
        // angle; the region turns about the whole picture's centre, not its own.
        var radians = placed.Rotation * Math.PI / 180.0;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        var rx = dx * cos - dy * sin;
        var ry = dx * sin + dy * cos;

        var region = placed.Clone();
        region.CenterX = placed.CenterX + rx / canvasWidth;
        region.CenterY = placed.CenterY + ry / canvasHeight;
        region.Width = placed.Width * (u1 - u0);
        region.Height = placed.Height * (v1 - v0);
        return new CropRegion(x0, y0, x1 - x0, y1 - y0, region);
    }
}

/// <summary>The visible pixels of a cropped picture and where they draw.</summary>
public sealed record CropRegion(int X, int Y, int Width, int Height, Transform Placement);
