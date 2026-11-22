using System.Globalization;
using System.Text.RegularExpressions;

namespace Temno.Generation;

/// <summary>One dropdown entry: the wire value a request sends and the text the panel shows.</summary>
public sealed record ResolutionChoice(string Value, string Label);

/// <summary>
/// The free width × height a model accepts, as the gateway enforces it. Areas are in pixels; the gateway
/// counts one megapixel as 2^20 pixels.
/// </summary>
public sealed record ResolutionSizeLimits(int MinSide, int MaxSide, long MinArea, long MaxArea, int MultipleOf,
    decimal MinAspect, decimal MaxAspect)
{
    public const long Megapixel = 1 << 20;

    public bool Admits(int width, int height) => Problem(width, height) is null;

    /// <summary>A plain sentence saying why the size is refused, or null when the model takes it.</summary>
    public string? Problem(int width, int height)
    {
        if (width < MinSide || height < MinSide || width > MaxSide || height > MaxSide)
            return $"Each side must be {MinSide} to {MaxSide} px.";
        if (MultipleOf > 1 && (width % MultipleOf != 0 || height % MultipleOf != 0))
            return $"Width and height must be multiples of {MultipleOf} px.";
        var area = (long)width * height;
        if (area > MaxArea) return $"That size is over this model's {Megapixels(MaxArea)} MP limit.";
        if (area < MinArea) return $"That size is under this model's {Megapixels(MinArea)} MP minimum.";
        var aspect = (decimal)width / height;
        if (aspect < MinAspect || aspect > MaxAspect) return "That shape is wider or taller than this model allows.";
        return null;
    }

    internal static string Megapixels(long area)
        => ((decimal)area / Megapixel).ToString("0.##", CultureInfo.InvariantCulture);
}

/// <summary>
/// What the Generate panel offers for a model's resolution setting (<c>size</c>, <c>resolution</c> or
/// <c>target_resolution</c>), as a dropdown (WP-419).
/// </summary>
/// <remarks>
/// An enumerated setting offers exactly its allowed values, in catalog order. A free <c>size</c> (the
/// catalog sends no allowed values and states its limits in the description, for example
/// "Sides 256..4096, area to 16MP") offers the common sizes that fit those limits, the default always
/// among them, plus a custom width × height checked against the same limits. The gateway re-checks
/// every value; this only keeps the panel from offering one it would refuse.
/// </remarks>
public sealed partial class ResolutionChoices
{
    /// <summary>The tag of the "Custom size…" entry. It is never sent.</summary>
    public const string Custom = "\u0000custom";

    // Common output sizes, by shape and then by area. Each free-size model shows the ones its limits admit.
    private static readonly (int W, int H)[] Presets =
    [
        (512, 512), (1024, 1024), (1536, 1536), (2048, 2048),
        (1024, 768), (1536, 1024), (1280, 720), (1920, 1080), (2560, 1440), (3840, 2160),
        (768, 1024), (1024, 1536), (720, 1280), (1080, 1920), (1440, 2560), (2160, 3840),
    ];

    private static readonly (int W, int H, string Name)[] Ratios =
    [
        (1, 1, "1:1"), (4, 3, "4:3"), (3, 4, "3:4"), (3, 2, "3:2"), (2, 3, "2:3"),
        (16, 9, "16:9"), (9, 16, "9:16"), (5, 4, "5:4"), (4, 5, "4:5"), (2, 1, "2:1"), (1, 2, "1:2"),
    ];

    private ResolutionChoices(IReadOnlyList<ResolutionChoice> choices, string selected, bool allowsCustom,
        ResolutionSizeLimits? limits, (int W, int H)? defaultSize, string hint)
    {
        Choices = choices;
        Selected = selected;
        AllowsCustom = allowsCustom;
        Limits = limits;
        DefaultSize = defaultSize;
        Hint = hint;
    }

    /// <summary>The dropdown's entries, without the custom entry. A value of "" means Automatic (omitted).</summary>
    public IReadOnlyList<ResolutionChoice> Choices { get; }

    /// <summary>The value selected when the popover opens: the model's default.</summary>
    public string Selected { get; }

    /// <summary>True when the model takes any width × height within <see cref="Limits"/>.</summary>
    public bool AllowsCustom { get; }

    /// <summary>The free-size limits read from the catalog, or null when they could not be read.</summary>
    public ResolutionSizeLimits? Limits { get; }

    /// <summary>The default size as width and height, when the default is one.</summary>
    public (int W, int H)? DefaultSize { get; }

    /// <summary>Tooltip copy for the setting, in plain words.</summary>
    public string Hint { get; }

    public static bool IsResolution(string name) => name is "size" or "resolution" or "target_resolution";

    /// <summary>
    /// The dropdown for a resolution setting, or null when the setting is not one or offers nothing to choose
    /// (the panel then keeps its text field).
    /// </summary>
    public static ResolutionChoices? For(string name, IReadOnlyList<string> allowedValues, string defaultValue, string description)
    {
        if (!IsResolution(name)) return null;
        var fallback = defaultValue is "" or "(omitted)" ? "" : defaultValue;
        if (allowedValues.Count > 0)
        {
            var choices = allowedValues.Select(v => new ResolutionChoice(v, Label(v))).ToList();
            if (!allowedValues.Contains(fallback, StringComparer.Ordinal))
            {
                choices.Insert(0, new ResolutionChoice("", "Automatic"));
                fallback = "";
            }
            var sized = allowedValues.All(v => ParseSize(v) is not null);
            return new ResolutionChoices(choices, fallback, false, null, ParseSize(fallback),
                sized ? "Output size in pixels" : "Output resolution");
        }
        if (name != "size") return null;

        var limits = ParseLimits(description);
        var defaultSize = ParseSize(fallback);
        var sizes = limits is null ? [] : Presets.Where(p => limits.Admits(p.W, p.H)).ToList();
        if (defaultSize is { } d && !sizes.Contains(d)) sizes.Insert(0, d);
        var list = sizes.Select(s => new ResolutionChoice(SizeValue(s.W, s.H), SizeLabel(s.W, s.H))).ToList();
        if (defaultSize is null)
        {
            list.Insert(0, new ResolutionChoice("", "Automatic"));
            fallback = "";
        }
        var hint = limits is null ? "Output size in pixels"
            : $"Output size in pixels. Each side {limits.MinSide} to {limits.MaxSide} px"
              + (limits.MultipleOf > 1 ? $" in steps of {limits.MultipleOf}" : "")
              + $", up to {ResolutionSizeLimits.Megapixels(limits.MaxArea)} MP"
              + (limits.MinArea > (long)limits.MinSide * limits.MinSide ? $" and at least {ResolutionSizeLimits.Megapixels(limits.MinArea)} MP" : "")
              + ".";
        return new ResolutionChoices(list, fallback, true, limits, defaultSize, hint);
    }

    /// <summary>
    /// The value the request sends for <paramref name="selected"/>; <paramref name="width"/> and
    /// <paramref name="height"/> are read only for the custom entry. Null means the setting is omitted.
    /// </summary>
    /// <exception cref="InvalidOperationException">A custom size the model does not take, with a plain reason.</exception>
    public string? Read(string? selected, double? width = null, double? height = null)
    {
        if (selected != Custom) return string.IsNullOrEmpty(selected) ? null : selected;
        if (!AllowsCustom) throw new InvalidOperationException("This model has no custom size.");
        if (width is not { } w || height is not { } h || w != Math.Floor(w) || h != Math.Floor(h) || w < 1 || h < 1
            || w > int.MaxValue || h > int.MaxValue)
            throw new InvalidOperationException("Enter a whole number of pixels for width and height.");
        if (Limits?.Problem((int)w, (int)h) is { } problem) throw new InvalidOperationException(problem);
        return SizeValue((int)w, (int)h);
    }

    public static string SizeValue(int width, int height)
        => width.ToString(CultureInfo.InvariantCulture) + "x" + height.ToString(CultureInfo.InvariantCulture);

    public static (int W, int H)? ParseSize(string? value)
    {
        var match = value is null ? null : SizePattern().Match(value);
        if (match is not { Success: true }
            || !int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var w)
            || !int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var h)
            || w < 1 || h < 1) return null;
        return (w, h);
    }

    /// <summary>"Square · 1024 × 1024", "Landscape 16:9 · 1920 × 1080"; tiers read "720p", "2K".</summary>
    public static string Label(string value)
    {
        if (ParseSize(value) is { } size) return SizeLabel(size.W, size.H);
        if (value == "auto") return "Automatic";
        return TierPattern().IsMatch(value) ? value.ToUpperInvariant() : value;
    }

    public static string SizeLabel(int width, int height)
    {
        var shape = width == height ? "Square" : width > height ? "Landscape" : "Portrait";
        var ratio = width == height ? null : Ratios.FirstOrDefault(r => (long)width * r.H == (long)height * r.W).Name;
        return shape + (ratio is null ? "" : " " + ratio) + " · "
            + width.ToString(CultureInfo.InvariantCulture) + " × " + height.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Reads the gateway's stated limits: "Sides 256..4096", then "area to 16MP" or "area 1MP..4MP", and the
    /// optional "multiples of 16" and "aspect 1:16..16:1". Without the sides there is nothing to check against.
    /// </summary>
    public static ResolutionSizeLimits? ParseLimits(string description)
    {
        var sides = SidesPattern().Match(description);
        if (!sides.Success || !int.TryParse(sides.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var min)
            || !int.TryParse(sides.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var max)
            || min < 1 || max < min) return null;
        long minArea = (long)min * min, maxArea = (long)max * max;
        var range = AreaRangePattern().Match(description);
        var cap = AreaCapPattern().Match(description);
        if (range.Success)
        {
            minArea = Pixels(range.Groups[1].Value);
            maxArea = Pixels(range.Groups[2].Value);
        }
        else if (cap.Success) maxArea = Pixels(cap.Groups[1].Value);
        var multiple = MultiplePattern().Match(description);
        var step = multiple.Success ? int.Parse(multiple.Groups[1].Value, CultureInfo.InvariantCulture) : 1;
        decimal minAspect = (decimal)min / max, maxAspect = (decimal)max / min;
        var aspect = AspectPattern().Match(description);
        if (aspect.Success)
        {
            minAspect = decimal.Parse(aspect.Groups[1].Value, CultureInfo.InvariantCulture) / decimal.Parse(aspect.Groups[2].Value, CultureInfo.InvariantCulture);
            maxAspect = decimal.Parse(aspect.Groups[3].Value, CultureInfo.InvariantCulture) / decimal.Parse(aspect.Groups[4].Value, CultureInfo.InvariantCulture);
        }
        return new ResolutionSizeLimits(min, max, minArea, maxArea, step, minAspect, maxAspect);
    }

    private static long Pixels(string megapixels)
        => (long)Math.Round(decimal.Parse(megapixels, CultureInfo.InvariantCulture) * ResolutionSizeLimits.Megapixel);

    [GeneratedRegex(@"^(\d+)x(\d+)$")] private static partial Regex SizePattern();
    [GeneratedRegex(@"^\d+(\.\d+)?k$", RegexOptions.IgnoreCase)] private static partial Regex TierPattern();
    [GeneratedRegex(@"Sides (\d+)\.\.(\d+)")] private static partial Regex SidesPattern();
    [GeneratedRegex(@"area (\d+(?:\.\d+)?)MP\.\.(\d+(?:\.\d+)?)MP")] private static partial Regex AreaRangePattern();
    [GeneratedRegex(@"area to (\d+(?:\.\d+)?)MP")] private static partial Regex AreaCapPattern();
    [GeneratedRegex(@"multiples of (\d+)")] private static partial Regex MultiplePattern();
    [GeneratedRegex(@"aspect (\d+):(\d+)\.\.(\d+):(\d+)")] private static partial Regex AspectPattern();
}
