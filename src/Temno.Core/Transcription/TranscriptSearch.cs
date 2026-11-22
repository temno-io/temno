namespace Temno.Core.Transcription;

/// <summary>
/// Exact keyword search over cached transcripts (Mac: TranscriptSearch.swift).
/// All query terms must appear in a segment (case/diacritic-insensitive).
/// </summary>
public static class TranscriptSearch
{
    public sealed record Hit(string MediaRef, double StartSeconds, double EndSeconds, string Text);

    public static IReadOnlyList<Hit> Search(
        string query,
        IEnumerable<(string MediaRef, TranscriptionResult Transcript)> assets,
        int limit = 20)
    {
        var terms = Terms(query);
        if (terms.Count == 0) return [];

        limit = Math.Clamp(limit <= 0 ? 20 : limit, 1, 100);
        var hits = new List<Hit>();
        foreach (var (mediaRef, transcript) in assets)
        {
            if (transcript.Segments.Count == 0 && !string.IsNullOrWhiteSpace(transcript.Text))
            {
                if (Matches(transcript.Text, terms))
                {
                    hits.Add(new Hit(mediaRef, 0, 0, transcript.Text));
                    if (hits.Count >= limit) return hits;
                }
                continue;
            }

            foreach (var segment in transcript.Segments)
            {
                if (!Matches(segment.Text, terms)) continue;
                hits.Add(new Hit(mediaRef, segment.Start, segment.End, segment.Text));
                if (hits.Count >= limit) return hits;
            }
        }

        return hits;
    }

    /// <summary>Query split into words, edge punctuation stripped (Mac: terms(in:)).</summary>
    public static IReadOnlyList<string> Terms(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        return query
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.Trim().Trim(',', '.', '!', '?', ';', ':', '"', '\'', '(', ')', '[', ']', '{', '}'))
            .Where(t => t.Length > 0)
            .ToList();
    }

    public static bool Matches(string text, IReadOnlyList<string> terms)
    {
        if (terms.Count == 0) return false;
        // InvariantCultureIgnoreCase approximates Mac diacriticInsensitive for ASCII/Latin.
        return terms.All(term =>
            text.Contains(term, StringComparison.InvariantCultureIgnoreCase));
    }
}
