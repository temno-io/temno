namespace Temno.Media.Text;

/// <summary>Maps Mac/CSS font names to Windows families without rewriting project JSON.</summary>
internal static class FontNameMap
{
    public static IEnumerable<string> CandidateFamilies(string? fontName)
    {
        var name = (fontName ?? "").Trim();
        if (IsAppleOrHelvetica(name))
        {
            yield return "Segoe UI";
            yield return "Segoe UI Semibold";
            yield return "Arial";
            yield break;
        }

        if (name.Length > 0)
        {
            yield return StripFaceSuffix(name);
            yield return name;
        }

        yield return "Segoe UI";
        yield return "Arial";
    }

    public static bool IsAppleOrHelvetica(string name)
    {
        if (name.Length == 0)
            return true;
        return name.StartsWith("Helvetica", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("SF Pro", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(".SF", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(".Apple", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Neue Haas", StringComparison.OrdinalIgnoreCase);
    }

    public static string StripFaceSuffix(string name)
    {
        foreach (var suffix in new[] { "-Bold", "-Semibold", "-Medium", "-Regular", "-Light", " Bold", " Semibold" })
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return name[..^suffix.Length].Trim();
        }

        return name;
    }

    public static bool ContainsBold(string? fontName)
        => (fontName ?? "").Contains("Bold", StringComparison.OrdinalIgnoreCase);
}
