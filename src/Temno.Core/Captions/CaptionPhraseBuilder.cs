using Temno.Core.Models;
using Temno.Core.Transcription;

namespace Temno.Core.Captions;

/// <summary>
/// Splits transcript segments into caption-sized phrases with per-word timing.
/// Mac: MediaPanel/CaptionsTab/CaptionBuilder.swift — ported line for line where possible.
/// </summary>
public static class CaptionPhraseBuilder
{
    public sealed record WordSpan(string Text, double Start, double End);

    public sealed record Phrase(string Text, double Start, double End, IReadOnlyList<WordSpan> Words)
    {
        public Phrase(string text, double start, double end) : this(text, start, end, []) { }
    }

    /// <summary>Split a transcript segment into chunks that pass <paramref name="fits"/> and the caps.</summary>
    /// <param name="maxCharacters">Mac add_captions maxCharacters: hard character cap per caption line.</param>
    public static List<Phrase> Phrases(
        TranscriptionSegment segment,
        IReadOnlyList<TranscriptionWord> words,
        Func<string, bool> fits,
        int? maxWords,
        double minDuration,
        int? maxCharacters = null)
    {
        var spoken = TranscriptionSpecials.StripSpecialTokens(segment.Text);
        if (spoken.Length == 0)
            return [];
        if (!string.Equals(spoken, segment.Text, StringComparison.Ordinal))
        {
            segment = new TranscriptionSegment
            {
                Text = spoken,
                Start = segment.Start,
                End = segment.End,
                Speaker = segment.Speaker,
            };
        }

        var accepts = fits;
        if (maxWords is { } wordLimit)
        {
            var cap = Math.Max(1, wordLimit);
            var inner = accepts;
            accepts = s => inner(s) && WordCount(s) <= cap;
        }
        if (maxCharacters is { } charLimit)
        {
            var cap = Math.Max(1, charLimit);
            var inner = accepts;
            accepts = s => inner(s) && s.Length <= cap;
        }
        var pieces = Split(segment.Text, accepts);
        var timed = Time(pieces, segment, words);
        return EnforceMinDuration(timed, minDuration);
    }

    public static List<Phrase> PhrasesFromTimedWords(
        IReadOnlyList<TranscriptionWord> words,
        Func<string, bool> fits,
        int? maxWords,
        double minDuration,
        int? maxCharacters = null)
    {
        var timed = words
            .Where(w => w.Start is not null && w.End is not null && !TranscriptionSpecials.IsSpecialToken(w.Text))
            .ToList();
        if (timed.Count == 0) return [];
        var start = timed[0].Start!.Value;
        var end = timed[^1].End!.Value;
        if (end <= start) return [];
        var text = string.Join(" ", timed.Select(w => w.Text)).Trim();
        if (text.Length == 0) return [];
        return Phrases(
            new TranscriptionSegment { Text = text, Start = start, End = end, Speaker = timed[0].Speaker },
            timed, fits, maxWords, minDuration, maxCharacters);
    }

    private static int WordCount(string text)
        => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private static List<string> Split(string text, Func<string, bool> fits)
    {
        var t = text.Trim();
        if (t.Length == 0) return [];
        if (fits(t)) return [t];
        var parts = BreakOnce(t);
        if (parts.Count <= 1) return [t]; // a single over-long word: keep it
        return parts.SelectMany(p => Split(p, fits)).ToList();
    }

    /// <summary>Break once at the best boundary present: sentence, then clause, then midpoint word.</summary>
    private static List<string> BreakOnce(string text)
        => BreakOn(text, ".!?") ?? BreakOn(text, ",;:") ?? BreakAtMidWord(text);

    /// <summary>Split after delimiters followed by a space, so "U.S." and "3.14" stay intact.</summary>
    private static List<string>? BreakOn(string text, string delimiters)
    {
        var pieces = new List<string>();
        var current = new System.Text.StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            current.Append(text[i]);
            var nextIsBreak = i + 1 >= text.Length || text[i + 1] == ' ';
            if (delimiters.Contains(text[i]) && nextIsBreak)
            {
                var piece = current.ToString().Trim();
                if (piece.Length > 0) pieces.Add(piece);
                current.Clear();
            }
        }
        var tail = current.ToString().Trim();
        if (tail.Length > 0) pieces.Add(tail);
        return pieces.Count > 1 ? pieces : null;
    }

    private static List<string> BreakAtMidWord(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length <= 1) return [text];
        var mid = words.Length / 2;
        return [string.Join(" ", words[..mid]), string.Join(" ", words[mid..])];
    }

    /// <summary>
    /// Time phrases from word runs by matching shared alphanumeric characters, so timing holds when
    /// runs don't split on spaces (contractions, split numbers, punctuation runs).
    /// </summary>
    private static List<Phrase> Time(
        List<string> texts, TranscriptionSegment segment, IReadOnlyList<TranscriptionWord> words)
    {
        var timed = words
            .Where(w => w.Start is not null && w.End is not null)
            .Select(w => (w.Text, Count: AlphanumericCount(w.Text), Start: w.Start!.Value, End: w.End!.Value))
            .Where(w => w.Count > 0)
            .ToList();
        if (timed.Count == 0) return Distribute(texts, segment.Start, segment.End);

        var phrases = new List<Phrase>();
        var idx = 0;
        foreach (var text in texts)
        {
            var want = AlphanumericCount(text);
            var got = 0;
            double? firstStart = null;
            double lastEnd = 0;
            var spans = new List<WordSpan>();
            while (idx < timed.Count && got < want)
            {
                var run = timed[idx];
                firstStart ??= run.Start;
                lastEnd = run.End;
                spans.Add(new WordSpan(run.Text.Trim(), run.Start, run.End));
                got += run.Count;
                idx++;
            }
            if (firstStart is not { } start) break;
            phrases.Add(new Phrase(text, start, lastEnd, spans));
        }
        return phrases.Count == texts.Count ? phrases : Distribute(texts, segment.Start, segment.End);
    }

    private static int AlphanumericCount(string text)
        => text.Count(char.IsLetterOrDigit);

    /// <summary>Share the segment's time across pieces by character count, back to back.</summary>
    private static List<Phrase> Distribute(List<string> texts, double start, double end)
    {
        if (texts.Count == 0) return [];
        var total = texts.Sum(t => Math.Max(t.Length, 1));
        var span = Math.Max(end - start, 0);
        var phrases = new List<Phrase>();
        var t = start;
        foreach (var text in texts)
        {
            var dur = span * Math.Max(text.Length, 1) / total;
            phrases.Add(new Phrase(text, t, t + dur));
            t += dur;
        }
        return phrases;
    }

    /// <summary>Give each phrase a floor duration without moving later phrases off their first word.</summary>
    private static List<Phrase> EnforceMinDuration(List<Phrase> phrases, double minDuration)
    {
        var outList = new List<Phrase>(phrases.Count);
        for (var i = 0; i < phrases.Count; i++)
        {
            var p = phrases[i];
            var targetEnd = Math.Max(p.End, p.Start + minDuration);
            if (i + 1 < phrases.Count)
            {
                targetEnd = Math.Min(targetEnd, phrases[i + 1].Start);
                if (targetEnd < p.Start) targetEnd = p.Start;
            }
            outList.Add(p with { End = targetEnd });
        }
        return outList;
    }
}
