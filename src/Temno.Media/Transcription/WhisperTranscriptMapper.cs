using Temno.Core.Transcription;

namespace Temno.Media.Transcription;

/// <summary>One Whisper token after converting t0/t1 centiseconds to seconds.</summary>
public readonly record struct WhisperMappedToken(string Text, double Start, double End);

/// <summary>One Whisper segment plus optional token-level times.</summary>
public sealed class WhisperMappedSegment
{
    public string Text { get; init; } = "";
    public double Start { get; init; }
    public double End { get; init; }
    public IReadOnlyList<WhisperMappedToken> Tokens { get; init; } = [];
}

/// <summary>
/// Maps Whisper.cpp / Whisper.net segments and tokens into Mac <see cref="TranscriptionResult"/>.
/// Token times are seconds (callers convert whisper centiseconds).
/// </summary>
public static class WhisperTranscriptMapper
{
    public static TranscriptionResult FromSegments(
        IEnumerable<WhisperMappedSegment> segments,
        string? language)
    {
        ArgumentNullException.ThrowIfNull(segments);

        var words = new List<TranscriptionWord>();
        var segs = new List<TranscriptionSegment>();
        foreach (var segment in segments)
        {
            var start = Math.Max(0, segment.Start);
            var end = Math.Max(start, segment.End);
            var text = TranscriptionSpecials.StripSpecialTokens(segment.Text);
            if (text.Length > 0)
            {
                segs.Add(new TranscriptionSegment
                {
                    Text = text,
                    Start = start,
                    End = end,
                });
            }

            var fromTokens = WordsFromTokens(segment.Tokens, start, end);
            if (fromTokens.Count > 0)
                words.AddRange(fromTokens);
            else if (text.Length > 0)
                words.AddRange(InterpolateWords(text, start, end));
        }

        var full = string.Join(" ", words.Select(w => w.Text).Where(t => t.Length > 0));
        if (full.Length == 0 && segs.Count > 0)
            full = string.Join(" ", segs.Select(s => s.Text));

        return new TranscriptionResult
        {
            Text = full,
            Language = language,
            Words = words,
            Segments = segs,
        };
    }

    public static double CentisecondsToSeconds(long centiseconds)
        => centiseconds / 100.0;

    public static bool IsSpecialToken(string? text)
        => TranscriptionSpecials.IsSpecialToken(text);

    private static List<TranscriptionWord> WordsFromTokens(
        IReadOnlyList<WhisperMappedToken> tokens,
        double segmentStart,
        double segmentEnd)
    {
        var words = new List<TranscriptionWord>();
        if (tokens.Count == 0)
            return words;

        string? currentText = null;
        double currentStart = segmentStart;
        double currentEnd = segmentStart;

        void Flush()
        {
            if (string.IsNullOrWhiteSpace(currentText))
                return;
            words.Add(new TranscriptionWord
            {
                Text = currentText.Trim(),
                Start = currentStart,
                End = Math.Max(currentStart, currentEnd),
            });
            currentText = null;
        }

        foreach (var token in tokens)
        {
            if (IsSpecialToken(token.Text))
                continue;

            var raw = token.Text;
            var piece = raw.Trim().TrimStart('Ġ');
            if (piece.Length == 0)
                continue;

            var t0 = token.Start;
            var t1 = token.End;
            if (t1 < t0)
                t1 = t0;
            if (t0 == 0 && t1 == 0)
            {
                t0 = currentText is null ? (words.Count == 0 ? segmentStart : currentEnd) : currentEnd;
                t1 = t0;
            }

            var newWord = currentText is null || StartsNewWord(raw);
            if (newWord)
            {
                Flush();
                currentText = piece;
                currentStart = t0 > 0 ? t0 : segmentStart;
                currentEnd = t1 > currentStart ? t1 : currentStart;
            }
            else
            {
                currentText += piece;
                if (t1 > currentEnd)
                    currentEnd = t1;
            }
        }

        Flush();
        if (words.Count > 0 && words[^1].End is 0 or null)
            words[^1].End = segmentEnd;
        return words;
    }

    private static bool StartsNewWord(string raw)
    {
        if (raw.Length == 0)
            return false;
        var c = raw[0];
        return char.IsWhiteSpace(c) || c == 'Ġ';
    }

    private static List<TranscriptionWord> InterpolateWords(string text, double start, double end)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return [];
        var span = Math.Max(0.01, end - start);
        var words = new List<TranscriptionWord>(parts.Length);
        for (var i = 0; i < parts.Length; i++)
        {
            var t0 = start + span * i / parts.Length;
            var t1 = start + span * (i + 1) / parts.Length;
            words.Add(new TranscriptionWord { Text = parts[i], Start = t0, End = t1 });
        }
        return words;
    }
}
