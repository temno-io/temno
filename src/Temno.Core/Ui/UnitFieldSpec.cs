namespace Temno.Core.Ui;

/// <summary>
/// Formatting, parsing, clamping and drag-scrub arithmetic for a unit-suffixed numeric field
/// (Mac <c>ScrubbableNumberField</c>: <c>48 pt</c>, <c>500 ms</c>, <c>100%</c>, <c>0 px</c>).
/// </summary>
/// <remarks>
/// Deliberately WinUI-free and immutable so <c>Temno.Core.Tests</c> can prove the behaviour that
/// matters — the App-side control is a shell that renders what this returns. See
/// <c>docs/DESIGN.md</c> §3 and WP-127.
/// </remarks>
public sealed record UnitFieldSpec
{
    /// <summary>Suffix shown after the value. <c>""</c> renders a bare number.</summary>
    public string Unit { get; init; } = "";

    public double Minimum { get; init; } = double.NegativeInfinity;

    public double Maximum { get; init; } = double.PositiveInfinity;

    /// <summary>Keyboard/wheel increment, and the grid values snap to. Must be &gt; 0.</summary>
    public double Step { get; init; } = 1;

    /// <summary>Digits shown after the decimal point.</summary>
    public int Decimals { get; init; }

    /// <summary>
    /// Drag sensitivity. A one-pixel horizontal drag changes the value by this much before
    /// snapping. Mac scrub fields are roughly one step per pixel; callers tune per field.
    /// </summary>
    public double UnitsPerPixel { get; init; } = 1;

    /// <summary>Fine-drag divisor applied when the modifier is held.</summary>
    public double FineDivisor { get; init; } = 10;

    /// <summary>The value a reset restores. Also decides whether the reset affordance shows.</summary>
    public double Default { get; init; }

    /// <summary>Separator between number and unit. <c>%</c> conventionally has none.</summary>
    public string UnitSeparator => Unit == "%" ? "" : " ";

    /// <summary><c>500</c> → <c>"500 ms"</c>; <c>100</c> → <c>"100%"</c>; <c>12</c> → <c>"12"</c>.</summary>
    public string Format(double value)
    {
        var number = Round(value).ToString("F" + Math.Max(0, Decimals), System.Globalization.CultureInfo.InvariantCulture);
        return Unit.Length == 0 ? number : number + UnitSeparator + Unit;
    }

    /// <summary>Number only, for the editable text of the field while it has focus.</summary>
    public string FormatEditable(double value)
        => Round(value).ToString("F" + Math.Max(0, Decimals), System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Accepts what <see cref="Format"/> produces, plus a bare number and a stray unit or '%'.
    /// Returns false for anything else — the caller then reverts to the last good value rather
    /// than committing a guess.
    /// </summary>
    /// <remarks>
    /// Typed input is clamped and rounded to <see cref="Decimals"/> but <b>not</b> snapped to
    /// <see cref="Step"/>: the step grid governs dragging and arrow keys, which are coarse gestures,
    /// while typing is an explicit request for that number. Snapping typed input would also make
    /// fine-drag results unreachable by keyboard, so the field could hold a value the user could not
    /// re-enter.
    /// </remarks>
    public bool TryParse(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var s = text.Trim();
        if (Unit.Length > 0 && s.EndsWith(Unit, StringComparison.OrdinalIgnoreCase))
            s = s[..^Unit.Length];
        s = s.TrimEnd().TrimEnd('%');

        if (!double.TryParse(
                s.Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            return false;
        if (double.IsNaN(parsed) || double.IsInfinity(parsed)) return false;

        value = Clamp(Round(parsed));
        return true;
    }

    public double Clamp(double value) => Math.Clamp(value, Minimum, Maximum);

    /// <summary>
    /// Snaps to the <see cref="Step"/> grid, measured from <see cref="Minimum"/> when it is finite
    /// so a range like 5…100 step 10 lands on 5, 15, 25 rather than 0, 10, 20.
    /// </summary>
    public double Quantize(double value) => Quantize(value, Step);

    /// <summary>
    /// Snaps to an explicit grid. Fine gestures pass <c>Step / FineDivisor</c> so holding the
    /// modifier actually reaches values between the coarse stops instead of being rounded back
    /// onto them.
    /// </summary>
    public double Quantize(double value, double step)
    {
        if (step <= 0 || double.IsNaN(step)) return Round(value);
        var origin = double.IsFinite(Minimum) ? Minimum : 0;
        var snapped = origin + (Math.Round((value - origin) / step, MidpointRounding.AwayFromZero) * step);
        return Round(snapped);
    }

    /// <summary>Grid a gesture snaps to: the coarse <see cref="Step"/>, or a tenth of it when fine.</summary>
    public double EffectiveStep(bool fine) => fine ? Step / Math.Max(1, FineDivisor) : Step;

    /// <summary>
    /// Value after dragging <paramref name="deltaPixels"/> horizontally from
    /// <paramref name="start"/>. Rightward (positive) increases. Quantized and clamped, so the
    /// result is always a value the field could otherwise have been typed to.
    /// </summary>
    public double Scrub(double start, double deltaPixels, bool fine = false)
    {
        var perPixel = UnitsPerPixel / (fine ? Math.Max(1, FineDivisor) : 1);
        return Clamp(Quantize(start + (deltaPixels * perPixel), EffectiveStep(fine)));
    }

    /// <summary>One <see cref="Step"/> up or down — keyboard arrows and the wheel share this.</summary>
    public double Nudge(double current, int steps, bool fine = false)
    {
        var size = EffectiveStep(fine);
        return Clamp(Quantize(current + (steps * size), size));
    }

    /// <summary>
    /// Whether the per-row reset affordance should show. Compared on the rounded, displayed value
    /// so a difference the user cannot see never offers a reset (<c>docs/DESIGN.md</c> §3).
    /// </summary>
    public bool DiffersFromDefault(double value)
        => Math.Abs(Round(value) - Round(Default)) > Epsilon;

    private double Round(double value)
        => Math.Round(value, Math.Clamp(Decimals, 0, 15), MidpointRounding.AwayFromZero);

    private double Epsilon => Math.Pow(10, -Math.Clamp(Decimals, 0, 15)) / 2;
}
