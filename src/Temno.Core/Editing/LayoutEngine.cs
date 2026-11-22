using Temno.Core.Models;

namespace Temno.Core.Editing;

/// <summary>Mac: Models/VideoLayout.swift LayoutRect. Normalized 0-1 canvas coordinates.</summary>
public readonly record struct LayoutRect(double X, double Y, double W, double H);

/// <summary>
/// Mac: Models/VideoLayout.swift LayoutSlot (the Mac calls Name "id").
/// Z is stacking order: higher Z renders on top (PiP inset is Z=1 over main Z=0).
/// </summary>
public sealed record LayoutSlot(string Name, LayoutRect Rect, int Z = 0);

/// <summary>Mac: Models/VideoLayout.swift LayoutFit (raw values "fill" / "fit").</summary>
public enum LayoutFit
{
    Fill,
    Fit,
}

/// <summary>Mac: Models/VideoLayout.swift VideoLayout (raw values are snake_case, see LayoutEngine.RawValue).</summary>
public enum LayoutKind
{
    Full,
    SideBySide,
    TopBottom,
    PipBottomRight,
    PipBottomLeft,
    PipTopRight,
    PipTopLeft,
    Grid2x2,
    MainSidebar,
    ThreeUp,
}

/// <summary>
/// Multi-video layout math for apply_layout.
/// Mac: Models/VideoLayout.swift (slot tables), EditorViewModel+Layout.swift layoutPlacement
/// (fill cover-crop / fit letterbox), EditorViewModel.cropFittingAspect + mediaCanvasAspect,
/// and ToolExecutor+Layout.swift layoutAnchors/resolveAnchor.
/// </summary>
public static class LayoutEngine
{
    // Mac: VideoLayout.pipInset / pipMargin.
    private const double PipInset = 0.28;
    private const double PipMargin = 0.035;

    // Mac: cropFittingAspect treats aspects within this tolerance as equal (no crop).
    private const double AspectTolerance = 0.0001;

    /// <summary>Mac: VideoLayout.allCases order.</summary>
    public static readonly IReadOnlyList<LayoutKind> AllKinds = new[]
    {
        LayoutKind.Full, LayoutKind.SideBySide, LayoutKind.TopBottom,
        LayoutKind.PipBottomRight, LayoutKind.PipBottomLeft, LayoutKind.PipTopRight, LayoutKind.PipTopLeft,
        LayoutKind.Grid2x2, LayoutKind.MainSidebar, LayoutKind.ThreeUp,
    };

    /// <summary>Mac: ToolExecutor.layoutAnchors.</summary>
    public static readonly IReadOnlyDictionary<string, (double X, double Y)> Anchors =
        new Dictionary<string, (double X, double Y)>
        {
            ["center"] = (0.5, 0.5),
            ["top"] = (0.5, 0),
            ["bottom"] = (0.5, 1),
            ["left"] = (0, 0.5),
            ["right"] = (1, 0.5),
            ["top_left"] = (0, 0),
            ["top_right"] = (1, 0),
            ["bottom_left"] = (0, 1),
            ["bottom_right"] = (1, 1),
        };

    private static readonly IReadOnlyList<LayoutSlot> FullSlots = new[]
    {
        new LayoutSlot("main", new LayoutRect(0, 0, 1, 1)),
    };

    private static readonly IReadOnlyList<LayoutSlot> SideBySideSlots = new[]
    {
        new LayoutSlot("left", new LayoutRect(0, 0, 0.5, 1)),
        new LayoutSlot("right", new LayoutRect(0.5, 0, 0.5, 1)),
    };

    private static readonly IReadOnlyList<LayoutSlot> TopBottomSlots = new[]
    {
        new LayoutSlot("top", new LayoutRect(0, 0, 1, 0.5)),
        new LayoutSlot("bottom", new LayoutRect(0, 0.5, 1, 0.5)),
    };

    private static readonly IReadOnlyList<LayoutSlot> PipBottomRightSlots =
        Pip(1 - PipMargin - PipInset, 1 - PipMargin - PipInset);

    private static readonly IReadOnlyList<LayoutSlot> PipBottomLeftSlots =
        Pip(PipMargin, 1 - PipMargin - PipInset);

    private static readonly IReadOnlyList<LayoutSlot> PipTopRightSlots =
        Pip(1 - PipMargin - PipInset, PipMargin);

    private static readonly IReadOnlyList<LayoutSlot> PipTopLeftSlots =
        Pip(PipMargin, PipMargin);

    private static readonly IReadOnlyList<LayoutSlot> Grid2x2Slots = new[]
    {
        new LayoutSlot("top_left", new LayoutRect(0, 0, 0.5, 0.5)),
        new LayoutSlot("top_right", new LayoutRect(0.5, 0, 0.5, 0.5)),
        new LayoutSlot("bottom_left", new LayoutRect(0, 0.5, 0.5, 0.5)),
        new LayoutSlot("bottom_right", new LayoutRect(0.5, 0.5, 0.5, 0.5)),
    };

    private static readonly IReadOnlyList<LayoutSlot> MainSidebarSlots = new[]
    {
        new LayoutSlot("main", new LayoutRect(0, 0, 0.7, 1)),
        new LayoutSlot("sidebar", new LayoutRect(0.7, 0, 0.3, 1)),
    };

    private static readonly IReadOnlyList<LayoutSlot> ThreeUpSlots = BuildThreeUp();

    private static IReadOnlyList<LayoutSlot> BuildThreeUp()
    {
        var third = 1.0 / 3.0;
        return new[]
        {
            new LayoutSlot("left", new LayoutRect(0, 0, third, 1)),
            new LayoutSlot("center", new LayoutRect(third, 0, third, 1)),
            new LayoutSlot("right", new LayoutRect(third * 2, 0, third, 1)),
        };
    }

    private static LayoutSlot[] Pip(double insetX, double insetY) => new[]
    {
        new LayoutSlot("main", new LayoutRect(0, 0, 1, 1), Z: 0),
        new LayoutSlot("inset", new LayoutRect(insetX, insetY, PipInset, PipInset), Z: 1),
    };

    public static string RawValue(LayoutKind kind) => kind switch
    {
        LayoutKind.Full => "full",
        LayoutKind.SideBySide => "side_by_side",
        LayoutKind.TopBottom => "top_bottom",
        LayoutKind.PipBottomRight => "pip_bottom_right",
        LayoutKind.PipBottomLeft => "pip_bottom_left",
        LayoutKind.PipTopRight => "pip_top_right",
        LayoutKind.PipTopLeft => "pip_top_left",
        LayoutKind.Grid2x2 => "grid_2x2",
        LayoutKind.MainSidebar => "main_sidebar",
        LayoutKind.ThreeUp => "three_up",
        _ => throw new ArgumentException($"unknown layout '{kind}'"),
    };

    /// <summary>Mac: VideoLayout(rawValue:) lookup with the apply_layout error message.</summary>
    public static LayoutKind ParseKind(string raw)
    {
        foreach (var kind in AllKinds)
        {
            if (RawValue(kind) == raw) return kind;
        }
        throw new ArgumentException(
            $"unknown layout '{raw}'. Valid: {string.Join(", ", AllKinds.Select(RawValue))}");
    }

    /// <summary>Mac: LayoutFit(rawValue: input.fit ?? "fill") with the apply_layout error message.</summary>
    public static LayoutFit ParseFit(string? raw) => (raw ?? "fill") switch
    {
        "fill" => LayoutFit.Fill,
        "fit" => LayoutFit.Fit,
        _ => throw new ArgumentException($"invalid fit '{raw}'. Valid: fill, fit"),
    };

    /// <summary>Mac: VideoLayout.slots — order and rects preserved exactly.</summary>
    public static IReadOnlyList<LayoutSlot> Slots(LayoutKind kind) => kind switch
    {
        LayoutKind.Full => FullSlots,
        LayoutKind.SideBySide => SideBySideSlots,
        LayoutKind.TopBottom => TopBottomSlots,
        LayoutKind.PipBottomRight => PipBottomRightSlots,
        LayoutKind.PipBottomLeft => PipBottomLeftSlots,
        LayoutKind.PipTopRight => PipTopRightSlots,
        LayoutKind.PipTopLeft => PipTopLeftSlots,
        LayoutKind.Grid2x2 => Grid2x2Slots,
        LayoutKind.MainSidebar => MainSidebarSlots,
        LayoutKind.ThreeUp => ThreeUpSlots,
        _ => throw new ArgumentException($"unknown layout '{kind}'"),
    };

    public static IReadOnlyList<string> SlotNames(LayoutKind kind) =>
        Slots(kind).Select(s => s.Name).ToList();

    /// <summary>
    /// Mac: ToolExecutor.resolveAnchor — named anchor (default "center") sets both axes,
    /// then anchorX/anchorY override individual axes; out-of-range values are errors.
    /// </summary>
    public static (double X, double Y) ResolveAnchor(string? anchor, double? anchorX, double? anchorY)
    {
        var a = Anchors["center"];
        if (anchor is not null)
        {
            if (!Anchors.TryGetValue(anchor, out a))
            {
                var valid = string.Join(", ", Anchors.Keys.OrderBy(k => k, StringComparer.Ordinal));
                throw new ArgumentException(
                    $"invalid anchor '{anchor}'. Valid: {valid}, or anchorX/anchorY for in-between values.");
            }
        }
        if (anchorX is { } ax && (ax < 0 || ax > 1))
            throw new ArgumentException($"anchorX must be between 0 and 1 (got {ax})");
        if (anchorY is { } ay && (ay < 0 || ay > 1))
            throw new ArgumentException($"anchorY must be between 0 and 1 (got {ay})");
        return (anchorX ?? a.X, anchorY ?? a.Y);
    }

    /// <summary>
    /// Compute the clip Transform + Crop that places a source into a named slot of a layout.
    /// Crop is always returned (identity in fit mode), mirroring the Mac, which assigns
    /// placement.crop to the clip unconditionally so stale crops are cleared.
    /// </summary>
    public static (Transform Transform, Crop Crop) Place(
        LayoutKind kind,
        string slot,
        double sourceW,
        double sourceH,
        int canvasW,
        int canvasH,
        string? fit = "fill",
        string? anchor = null,
        double? anchorX = null,
        double? anchorY = null)
    {
        var slots = Slots(kind);
        var s = slots.FirstOrDefault(x => x.Name == slot)
            ?? throw new ArgumentException(
                $"'{slot}' is not a slot of layout '{RawValue(kind)}'. Slots: {string.Join(", ", slots.Select(x => x.Name))}");
        var parsedFit = ParseFit(fit);
        var (ax, ay) = ResolveAnchor(anchor, anchorX, anchorY);
        return PlaceInRect(s.Rect, parsedFit, sourceW, sourceH, canvasW, canvasH, ax, ay);
    }

    /// <summary>Mac: EditorViewModel.layoutPlacement(for:in:fit:anchorX:anchorY:).</summary>
    public static (Transform Transform, Crop Crop) PlaceInRect(
        LayoutRect rect,
        LayoutFit fit,
        double sourceW,
        double sourceH,
        int canvasW,
        int canvasH,
        double anchorX = 0.5,
        double anchorY = 0.5)
    {
        var canvasAspect = canvasW / (double)Math.Max(1, canvasH);
        var slotPixelAspect = rect.H > 0 ? (rect.W / rect.H) * canvasAspect : canvasAspect;

        if (fit == LayoutFit.Fill)
        {
            var crop = CropFittingAspect(sourceW, sourceH, slotPixelAspect, anchorX, anchorY);
            var vw = Math.Max(0, 1 - crop.Left - crop.Right);
            var vh = Math.Max(0, 1 - crop.Top - crop.Bottom);
            if (vw <= 0 || vh <= 0)
                return (Transform.FromTopLeft(rect.X, rect.Y, rect.W, rect.H), crop);
            // Oversize the transform so the slot shows exactly the uncropped region.
            var w = rect.W / vw;
            var h = rect.H / vh;
            var x = rect.X - crop.Left * w;
            var y = rect.Y - crop.Top * h;
            return (Transform.FromTopLeft(x, y, w, h), crop);
        }

        var rel = MediaCanvasAspect(sourceW, sourceH, canvasW, canvasH);
        if (rel is not > 0)
            return (Transform.FromTopLeft(rect.X, rect.Y, rect.W, rect.H), new Crop());
        double drawW, drawH;
        if (rel.Value * rect.H <= rect.W)
        {
            drawH = rect.H;
            drawW = rel.Value * rect.H;
        }
        else
        {
            drawW = rect.W;
            drawH = rect.W / rel.Value;
        }
        var fitAx = Math.Clamp(anchorX, 0, 1);
        var fitAy = Math.Clamp(anchorY, 0, 1);
        var fx = rect.X + (rect.W - drawW) * fitAx;
        var fy = rect.Y + (rect.H - drawH) * fitAy;
        return (Transform.FromTopLeft(fx, fy, drawW, drawH), new Crop());
    }

    /// <summary>
    /// Mac: EditorViewModel.cropFittingAspect — edge insets that trim the source to the
    /// target pixel aspect; the anchor picks which part of the source stays visible.
    /// </summary>
    public static Crop CropFittingAspect(
        double sourceW,
        double sourceH,
        double targetPixelAspect,
        double anchorX = 0.5,
        double anchorY = 0.5)
    {
        if (sourceW <= 0 || sourceH <= 0 || targetPixelAspect <= 0) return new Crop();
        var sourceAspect = sourceW / sourceH;
        if (Math.Abs(sourceAspect - targetPixelAspect) < AspectTolerance) return new Crop();
        var ax = Math.Clamp(anchorX, 0, 1);
        var ay = Math.Clamp(anchorY, 0, 1);
        if (sourceAspect > targetPixelAspect)
        {
            var total = 1 - targetPixelAspect / sourceAspect;
            var left = total * ax;
            return new Crop { Left = left, Right = total - left };
        }
        else
        {
            var total = 1 - sourceAspect / targetPixelAspect;
            var top = total * ay;
            return new Crop { Top = top, Bottom = total - top };
        }
    }

    /// <summary>Mac: EditorViewModel.mediaCanvasAspect — source aspect relative to canvas aspect.</summary>
    public static double? MediaCanvasAspect(double sourceW, double sourceH, int canvasW, int canvasH)
    {
        if (sourceW <= 0 || sourceH <= 0 || canvasW <= 0 || canvasH <= 0) return null;
        var canvasAspect = canvasW / (double)canvasH;
        return (sourceW / sourceH) / canvasAspect;
    }

    /// <summary>Mac: <see cref="MediaCanvasAspect(double, double, int, int)"/> for a library entry; null without probed dimensions.</summary>
    public static double? MediaCanvasAspect(MediaManifestEntry? entry, int canvasW, int canvasH)
        => entry?.SourceWidth is int w && entry.SourceHeight is int h
            ? MediaCanvasAspect(w, h, canvasW, canvasH)
            : null;

    // Mac: Defaults.aspectTolerance — looser than cropFittingAspect's, so near-canvas sources fill exactly.
    // The renderer draws within the same tolerance (WP-405), so a fill placed here is never fitted again.
    private const double FitAspectTolerance = ClipPlacement.AspectTolerance;

    /// <summary>
    /// Mac: EditorViewModel.fitTransform — the transform a newly placed clip starts with: the whole
    /// source, centred, at its own aspect. A default <see cref="Transform"/> fills the canvas, which
    /// stretches any source whose aspect differs; unknown dimensions still get that default.
    /// </summary>
    public static Transform FitTransform(double sourceW, double sourceH, int canvasW, int canvasH)
    {
        if (MediaCanvasAspect(sourceW, sourceH, canvasW, canvasH) is not { } relative) return new Transform();
        var canvasAspect = canvasW / (double)canvasH;
        if (Math.Abs(canvasAspect - relative * canvasAspect) < FitAspectTolerance) return new Transform();
        return relative > 1
            ? new Transform { Width = 1, Height = 1 / relative }
            : new Transform { Width = relative, Height = 1 };
    }

    /// <summary><see cref="FitTransform(double, double, int, int)"/> for a library entry placed on <paramref name="timeline"/>.</summary>
    public static Transform FitTransform(MediaManifestEntry? entry, Timeline timeline)
        => entry?.SourceWidth is int w && entry.SourceHeight is int h
            ? FitTransform(w, h, timeline.Width, timeline.Height)
            : new Transform();
}
