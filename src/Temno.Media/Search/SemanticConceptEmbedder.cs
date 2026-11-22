using System.Text.RegularExpressions;

namespace Temno.Media.Search;

/// <summary>
/// Lightweight on-device text↔frame concept space for free-text Moments queries.
/// Projects free text and VisualSignature means into a shared unit vector without ML runtime.
/// Not SigLIP quality — documented interim until ONNX SigLIP packages ship.
/// </summary>
public sealed class SemanticConceptEmbedder : ISemanticVisualEmbedder
{
    private static readonly Regex Words = new("[a-z0-9]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Concept axes (order is embedding layout).
    private static readonly string[] Concepts =
    [
        "red", "orange", "yellow", "green", "cyan", "blue", "purple", "pink", "brown",
        "black", "white", "gray",
        "dark", "bright", "highcontrast", "lowcontrast",
        "sky", "grass", "water", "fire", "snow", "night", "day", "warm", "cool", "nature", "indoor",
    ];

    private static readonly IReadOnlyDictionary<string, int> Index =
        Concepts.Select((c, i) => (c, i)).ToDictionary(x => x.c, x => x.i, StringComparer.Ordinal);

    // Free-text aliases → concept weights.
    private static readonly IReadOnlyDictionary<string, (string Concept, float Weight)[]> TextMap =
        new Dictionary<string, (string, float)[]>(StringComparer.Ordinal)
        {
            ["red"] = [("red", 1f)],
            ["orange"] = [("orange", 1f)],
            ["yellow"] = [("yellow", 1f)],
            ["green"] = [("green", 1f)],
            ["cyan"] = [("cyan", 1f)],
            ["blue"] = [("blue", 1f)],
            ["purple"] = [("purple", 1f)], ["violet"] = [("purple", 1f)],
            ["pink"] = [("pink", 1f)],
            ["brown"] = [("brown", 1f)],
            ["black"] = [("black", 1f)],
            ["white"] = [("white", 1f)],
            ["gray"] = [("gray", 1f)], ["grey"] = [("gray", 1f)],
            ["dark"] = [("dark", 1f)], ["dim"] = [("dark", 0.9f)], ["night"] = [("night", 1f), ("dark", 0.8f)],
            ["bright"] = [("bright", 1f)], ["light"] = [("bright", 0.7f)], ["day"] = [("day", 1f), ("bright", 0.6f)],
            ["sky"] = [("sky", 1f), ("blue", 0.7f), ("bright", 0.3f)],
            ["ocean"] = [("water", 1f), ("blue", 0.7f), ("cyan", 0.4f)],
            ["sea"] = [("water", 1f), ("blue", 0.7f)],
            ["water"] = [("water", 1f), ("blue", 0.5f), ("cyan", 0.4f)],
            ["grass"] = [("grass", 1f), ("green", 0.9f), ("nature", 0.5f)],
            ["tree"] = [("green", 0.7f), ("nature", 0.8f)], ["forest"] = [("green", 0.8f), ("nature", 1f)],
            ["fire"] = [("fire", 1f), ("red", 0.7f), ("orange", 0.8f), ("warm", 0.6f)],
            ["sunset"] = [("orange", 0.7f), ("red", 0.5f), ("warm", 0.8f), ("bright", 0.3f)],
            ["snow"] = [("snow", 1f), ("white", 0.9f), ("bright", 0.4f)],
            ["sand"] = [("yellow", 0.5f), ("brown", 0.4f), ("warm", 0.3f)],
            ["car"] = [], // structural word — ignored alone
            ["person"] = [], ["people"] = [], ["man"] = [], ["woman"] = [],
            ["warm"] = [("warm", 1f), ("orange", 0.3f), ("red", 0.2f)],
            ["cool"] = [("cool", 1f), ("blue", 0.4f), ("cyan", 0.3f)],
            ["indoor"] = [("indoor", 1f)],
            ["outdoor"] = [("nature", 0.5f), ("bright", 0.3f)],
            ["contrast"] = [("highcontrast", 0.6f)],
        };

    public const string ModelIdValue = "concept-space-v1";
    public const string DefaultFallbackMessage =
        "SigLIP2 ONNX is not installed. Moments search is using concept-space-v1.";

    public SemanticConceptEmbedder(string? message = null)
    {
        Message = string.IsNullOrWhiteSpace(message) ? DefaultFallbackMessage : message;
    }

    public string ModelId => ModelIdValue;
    public int EmbeddingDim => Concepts.Length;
    public SemanticEmbedderStatus Status => SemanticEmbedderStatus.Ready;
    public SemanticSearchMode Mode => SemanticSearchMode.Fallback;
    public string? Message { get; }
    public bool EncodesPixels => false;

    public float[]? EncodeFrame(VideoFrame frame) => null;

    public float[]? EncodeText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var vec = new float[Concepts.Length];
        var lower = text.ToLowerInvariant();
        foreach (Match m in Words.Matches(lower))
        {
            if (!TextMap.TryGetValue(m.Value, out var maps)) continue;
            foreach (var (concept, weight) in maps)
            {
                if (Index.TryGetValue(concept, out var i))
                    vec[i] += weight;
            }
        }

        // Hex colors in free text
        var hex = Regex.Match(lower, @"#([0-9a-f]{6}|[0-9a-f]{3})");
        if (hex.Success)
            AccumulateColor(vec, ParseHex(hex.Groups[1].Value), 1f);

        if (lower.Contains("high contrast", StringComparison.Ordinal))
            vec[Index["highcontrast"]] += 1f;
        if (lower.Contains("low contrast", StringComparison.Ordinal))
            vec[Index["lowcontrast"]] += 1f;

        return Normalize(vec);
    }

    public float[]? EncodeSignature(VisualSignature signature)
    {
        ArgumentNullException.ThrowIfNull(signature);
        var vec = new float[Concepts.Length];
        AccumulateColor(vec, new VisualColor(signature.MeanRed, signature.MeanGreen, signature.MeanBlue), 1.2f);

        if (signature.MeanLuminance < 0.28) vec[Index["dark"]] += 1f;
        else if (signature.MeanLuminance > 0.72) vec[Index["bright"]] += 1f;

        if (signature.LuminanceContrast > 0.35) vec[Index["highcontrast"]] += 0.8f;
        else if (signature.LuminanceContrast < 0.08) vec[Index["lowcontrast"]] += 0.8f;

        // Weak scene cues from dominant color + luminance
        if (signature.MeanBlue > 0.45 && signature.MeanBlue >= signature.MeanRed && signature.MeanLuminance > 0.35)
        {
            vec[Index["sky"]] += 0.5f;
            vec[Index["water"]] += 0.25f;
        }
        if (signature.MeanGreen > 0.4 && signature.MeanGreen >= signature.MeanRed)
        {
            vec[Index["grass"]] += 0.5f;
            vec[Index["nature"]] += 0.4f;
        }
        if (signature.MeanRed > 0.5 && signature.MeanGreen > 0.25 && signature.MeanBlue < 0.35)
            vec[Index["fire"]] += 0.35f;
        if (signature.MeanLuminance > 0.85 && signature.MeanRed > 0.8 && signature.MeanGreen > 0.8 && signature.MeanBlue > 0.8)
            vec[Index["snow"]] += 0.4f;
        if (signature.MeanLuminance < 0.2)
            vec[Index["night"]] += 0.5f;
        else if (signature.MeanLuminance > 0.55)
            vec[Index["day"]] += 0.3f;

        var warmth = signature.MeanRed - signature.MeanBlue;
        if (warmth > 0.15) vec[Index["warm"]] += 0.5f;
        if (warmth < -0.15) vec[Index["cool"]] += 0.5f;

        return Normalize(vec);
    }

    public static double Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0;
        double dot = 0;
        for (var i = 0; i < a.Length; i++)
            dot += a[i] * b[i];
        return Math.Clamp(dot, -1, 1);
    }

    private static void AccumulateColor(float[] vec, VisualColor color, float weight)
    {
        // Soft assignment to named colors by inverse distance
        var named = new (string Name, VisualColor C)[]
        {
            ("red", new(1, 0, 0)), ("orange", new(1, 0.5, 0)), ("yellow", new(1, 1, 0)),
            ("green", new(0, 0.7, 0)), ("cyan", new(0, 0.85, 0.85)), ("blue", new(0, 0, 1)),
            ("purple", new(0.5, 0, 0.75)), ("pink", new(1, 0.35, 0.6)), ("brown", new(0.42, 0.24, 0.1)),
            ("black", new(0, 0, 0)), ("white", new(1, 1, 1)), ("gray", new(0.5, 0.5, 0.5)),
        };
        var scores = new double[named.Length];
        double sum = 0;
        for (var i = 0; i < named.Length; i++)
        {
            var dr = named[i].C.Red - color.Red;
            var dg = named[i].C.Green - color.Green;
            var db = named[i].C.Blue - color.Blue;
            var d = Math.Sqrt(dr * dr + dg * dg + db * db) / Math.Sqrt(3.0);
            scores[i] = 1.0 / (0.08 + d * d);
            sum += scores[i];
        }
        if (sum <= 0) return;
        for (var i = 0; i < named.Length; i++)
            vec[Index[named[i].Name]] += (float)(weight * scores[i] / sum);
    }

    private static VisualColor ParseHex(string hex)
    {
        if (hex.Length == 3)
            hex = string.Concat(hex.Select(c => new string(c, 2)));
        return new VisualColor(
            Convert.ToInt32(hex[..2], 16) / 255.0,
            Convert.ToInt32(hex[2..4], 16) / 255.0,
            Convert.ToInt32(hex[4..6], 16) / 255.0);
    }

    private static float[]? Normalize(float[] vec)
    {
        double sumSq = 0;
        for (var i = 0; i < vec.Length; i++)
            sumSq += vec[i] * vec[i];
        if (sumSq < 1e-12) return null;
        var inv = (float)(1.0 / Math.Sqrt(sumSq));
        for (var i = 0; i < vec.Length; i++)
            vec[i] *= inv;
        return vec;
    }
}
