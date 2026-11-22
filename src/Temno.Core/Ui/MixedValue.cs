namespace Temno.Core.Ui;

/// <summary>
/// WP-166. Mac <c>sharedClipValue</c>: a selection of equal values is that value; any disagreement
/// is mixed and the Inspector shows <see cref="Marker"/>. WinUI-free so Core tests pin the rule
/// the field shell renders.
/// </summary>
public static class MixedValue
{
    /// <summary>Mac Inspector mixed glyph. An em dash, not a hyphen.</summary>
    public const string Marker = "\u2014";

    public static bool TryShared<T>(IReadOnlyList<T> values, out T shared)
        => TryShared(values, EqualityComparer<T>.Default, out shared);

    public static bool TryShared<T>(IReadOnlyList<T> values, IEqualityComparer<T>? comparer, out T shared)
    {
        shared = default!;
        if (values is null || values.Count == 0) return false;
        comparer ??= EqualityComparer<T>.Default;
        var first = values[0];
        for (var i = 1; i < values.Count; i++)
        {
            if (!comparer.Equals(values[i], first))
                return false;
        }

        shared = first;
        return true;
    }

    /// <summary>
    /// Shared when every value rounds to the same displayed number. Two opacities that both paint
    /// as <c>50%</c> are not mixed, matching <see cref="UnitFieldSpec"/>'s display rounding.
    /// </summary>
    public static bool TrySharedRounded(IReadOnlyList<double> values, int decimals, out double shared)
    {
        shared = 0;
        if (values is null || values.Count == 0) return false;
        var places = Math.Clamp(decimals, 0, 15);
        var first = Round(values[0], places);
        var epsilon = Math.Pow(10, -places) / 2;
        for (var i = 1; i < values.Count; i++)
        {
            if (Math.Abs(Round(values[i], places) - first) > epsilon)
                return false;
        }

        shared = first;
        return true;
    }

    private static double Round(double value, int decimals)
        => Math.Round(value, decimals, MidpointRounding.AwayFromZero);
}
