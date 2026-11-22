namespace Temno.Core.Session;

public sealed class EffectEntry
{
    public string Type { get; set; } = "";
    public Dictionary<string, double>? Params { get; set; }
    public bool? Enabled { get; set; }

    /// <summary>
    /// Final zero-based stack index. Null keeps an existing effect where it is and inserts a
    /// new effect at the catalog position.
    /// </summary>
    public int? Position { get; set; }
}
