using System.Text.RegularExpressions;

namespace Temno.Media.Search;

/// <summary>
/// Visual query grammar: color/brightness/similar (WP-46) plus free-text Moments (WP-95).
/// </summary>
public static class VisualSearchQueryParser
{
    private static readonly Regex HexColor = new(
        @"(?<![0-9A-Za-z])#(?<hex>[0-9A-Fa-f]{6}|[0-9A-Fa-f]{3})(?![0-9A-Za-z])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Words = new("[a-z]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly IReadOnlyDictionary<string, VisualColor> NamedColors =
        new Dictionary<string, VisualColor>(StringComparer.OrdinalIgnoreCase)
        {
            ["red"] = new(1, 0, 0),
            ["orange"] = new(1, 0.5, 0),
            ["yellow"] = new(1, 1, 0),
            ["green"] = new(0, 0.7, 0),
            ["blue"] = new(0, 0, 1),
            ["purple"] = new(0.5, 0, 0.75),
            ["pink"] = new(1, 0.35, 0.6),
            ["cyan"] = new(0, 0.85, 0.85),
            ["teal"] = new(0, 0.5, 0.5),
            ["brown"] = new(0.42, 0.24, 0.1),
            ["black"] = new(0, 0, 0),
            ["white"] = new(1, 1, 1),
            ["gray"] = new(0.5, 0.5, 0.5),
            ["grey"] = new(0.5, 0.5, 0.5),
        };

    private static readonly HashSet<string> AllowedTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "dark", "dim", "bright", "light", "high", "low", "contrast", "color", "colour", "and",
        "red", "orange", "yellow", "green", "blue", "purple", "pink", "cyan", "teal", "brown",
        "black", "white", "gray", "grey",
    };

    public const string SupportedGrammar =
        "#RRGGBB, named colors, dark/bright, high/low contrast, similar:<mediaRef>, free-text Moments, or spoken keywords";

    public static bool TryParse(string? input, out VisualSearchQuery? query, out string? error)
    {
        query = null;
        error = null;
        var text = input?.Trim() ?? "";
        if (text.Length == 0)
        {
            error = $"A visual query is required. Supported: {SupportedGrammar}.";
            return false;
        }

        if (text.StartsWith("similar:", StringComparison.OrdinalIgnoreCase))
        {
            var mediaRef = text["similar:".Length..].Trim();
            if (mediaRef.Length == 0)
            {
                error = "similar: requires a mediaRef.";
                return false;
            }

            query = new VisualSearchQuery
            {
                Kind = VisualQueryKind.Similar,
                ReferenceMediaRef = mediaRef,
            };
            return true;
        }

        var lower = text.ToLowerInvariant();
        var hexMatch = HexColor.Match(lower);
        VisualColor? color = null;
        if (hexMatch.Success)
            color = ParseHex(hexMatch.Groups["hex"].Value);

        var words = Words.Matches(HexColor.Replace(lower, " "))
            .Select(match => match.Value)
            .ToList();

        // Pure visual grammar (WP-46): only allowed tokens.
        if (words.Count == 0 || words.All(word => AllowedTokens.Contains(word)))
        {
            var named = words.FirstOrDefault(word => NamedColors.ContainsKey(word));
            if (color is null && named is not null)
                color = NamedColors[named];

            var hasDark = words.Any(word => word is "dark" or "dim");
            var hasBright = words.Any(word => word is "bright" or "light");
            if (hasDark && hasBright)
            {
                error = "Choose either a dark/dim or bright/light cue, not both.";
                return false;
            }

            var hasHighContrast = HasPhrase(words, "high", "contrast");
            var hasLowContrast = HasPhrase(words, "low", "contrast");
            if (hasHighContrast && hasLowContrast)
            {
                error = "Choose either high contrast or low contrast, not both.";
                return false;
            }

            var targetLuminance = hasDark ? 0.18 : hasBright ? 0.82 : (double?)null;
            var targetContrast = hasHighContrast ? 0.45 : hasLowContrast ? 0.05 : (double?)null;
            if (color is null && targetLuminance is null && targetContrast is null)
            {
                // Empty of visual cues → treat as semantic free text if any non-empty string.
                query = new VisualSearchQuery
                {
                    Kind = VisualQueryKind.Semantic,
                    TextQuery = text,
                };
                return true;
            }

            query = new VisualSearchQuery
            {
                Kind = VisualQueryKind.Visual,
                TargetColor = color,
                TargetLuminance = targetLuminance,
                TargetContrast = targetContrast,
            };
            return true;
        }

        // Free-text Moments (WP-95)
        query = new VisualSearchQuery
        {
            Kind = VisualQueryKind.Semantic,
            TextQuery = text,
        };
        return true;
    }

    private static VisualColor ParseHex(string hex)
    {
        if (hex.Length == 3)
            hex = string.Concat(hex.Select(value => new string(value, 2)));

        return new VisualColor(
            Convert.ToInt32(hex[..2], 16) / 255.0,
            Convert.ToInt32(hex[2..4], 16) / 255.0,
            Convert.ToInt32(hex[4..6], 16) / 255.0);
    }

    private static bool HasPhrase(IReadOnlyList<string> words, string first, string second)
    {
        for (var i = 0; i < words.Count - 1; i++)
        {
            if (words[i] == first && words[i + 1] == second)
                return true;
        }
        return false;
    }
}
