using System.Text.Json;
using Temno.Core.Models;

namespace Temno.Media.Text;

/// <summary>
/// Pure per-frame evaluator for text animation. Port of Mac <c>Compositing/TextAnimator.swift</c>
/// (@ cfe9c18), including its token↔timing alignment from <c>TextFrameRenderer.tokenTimings</c>.
/// <para>
/// Entrance presets animate the clip as a whole. Per-word presets return one
/// <see cref="WordRender"/> per whitespace token, each with its own opacity, scale, vertical offset,
/// colour and optional background block — the rasterizer draws them individually.
/// </para>
/// </summary>
public static class TextAnimator
{
    /// <summary>One word's draw state. Mac TextAnimator.WordState plus the token's character span.</summary>
    /// <param name="OffsetY">Vertical offset in multiples of the font size, positive = down.</param>
    public readonly record struct WordRender(
        int Index,
        int Start,
        int Length,
        double Opacity,
        double Scale,
        double OffsetY,
        TextStyle.RGBA Color,
        TextStyle.RGBA? Background);

    /// <param name="OffsetY">Whole-clip offset as a fraction of canvas height, positive = down.</param>
    /// <param name="Words">
    /// Per-word states for per-word presets, in reading order and already filtered to visible words.
    /// Null for entrance and typewriter presets, where the line is drawn in one pass.
    /// </param>
    public readonly record struct ClipState(
        double Opacity,
        double Scale,
        double OffsetY,
        string? VisibleText,
        IReadOnlyList<WordRender>? Words = null);

    public static TextAnimation? ReadAnimation(Clip clip)
    {
        if (clip.TextAnimation is not { } el) return null;
        try
        {
            return JsonSerializer.Deserialize<TextAnimation>(el.GetRawText(), JsonOptions.ForProject);
        }
        catch
        {
            return null;
        }
    }

    /// <param name="rel">Frames since clip start.</param>
    /// <param name="wordTimings">Clip-relative karaoke timings; null falls back to an even split.</param>
    /// <param name="baseColor">The clip's static text colour, which per-word states tint away from.</param>
    public static ClipState Evaluate(
        TextAnimation? anim,
        string fullText,
        int rel,
        int clipDurationFrames,
        IReadOnlyList<WordTiming>? wordTimings = null,
        TextStyle.RGBA? baseColor = null)
    {
        fullText ??= "";
        if (anim is null || string.IsNullOrEmpty(anim.Preset) || anim.Preset is "none" or "off")
            return new ClipState(1, 1, 0, fullText);

        switch (TextAnimation.ModeOf(anim.Preset))
        {
            case TextAnimation.RenderMode.Typewriter:
                return new ClipState(1, 1, 0, Typewriter(fullText, rel, anim.PerWordFrames));

            case TextAnimation.RenderMode.PerWord:
                return PerWordState(anim, fullText, rel, clipDurationFrames, wordTimings, baseColor);

            default:
                return ClipEntry(anim, fullText, rel);
        }
    }

    /// <summary>Mac TextAnimator.clipEntry — the whole-clip entrance, timed off perWordFrames.</summary>
    private static ClipState ClipEntry(TextAnimation anim, string fullText, int rel)
    {
        var dur = Math.Max(1, anim.PerWordFrames);
        var t = Progress(rel, 0, dur);
        return anim.Preset switch
        {
            "fadeIn" => new ClipState(t, 1, 0, fullText),
            "popIn" => new ClipState(t, 0.6 + 0.4 * t, 0, fullText),
            "slideUp" => new ClipState(t, 1, 0.05 * (1 - t), fullText),
            _ => new ClipState(1, 1, 0, fullText),
        };
    }

    private static ClipState PerWordState(
        TextAnimation anim,
        string fullText,
        int rel,
        int clipDurationFrames,
        IReadOnlyList<WordTiming>? wordTimings,
        TextStyle.RGBA? baseColor)
    {
        var tokens = WordSpans(fullText);
        if (tokens.Count == 0) return new ClipState(1, 1, 0, fullText);

        var basis = baseColor ?? new TextStyle.RGBA { R = 1, G = 1, B = 1, A = 1 };
        var timings = TokenTimings(fullText, tokens, wordTimings, clipDurationFrames);

        var words = new List<WordRender>(tokens.Count);
        for (var i = 0; i < tokens.Count; i++)
        {
            var state = WordState(anim, timings[i], rel, basis);
            // Mac skips a word whose opacity has not come up yet rather than drawing it transparent.
            if (state.Opacity <= 0) continue;
            words.Add(new WordRender(
                i, tokens[i].Start, tokens[i].Length,
                state.Opacity, state.Scale, state.OffsetY, state.Color, state.Background));
        }
        return new ClipState(1, 1, 0, fullText, words);
    }

    private readonly record struct WordDraw(
        double Opacity, double Scale, double OffsetY, TextStyle.RGBA Color, TextStyle.RGBA? Background);

    /// <summary>Mac TextAnimator.wordState.</summary>
    private static WordDraw WordState(TextAnimation anim, WordTiming word, int rel, TextStyle.RGBA basis)
    {
        var highlight = anim.Highlight ?? TextAnimation.DefaultHighlight;
        var hand = Math.Max(1, anim.PerWordFrames);

        switch (anim.Preset)
        {
            case "wordReveal":
            {
                var t = Progress(rel, word.StartFrame, hand);
                return new WordDraw(t, 1, 0, ActiveTint(anim, word, rel, basis), null);
            }
            case "wordSlide":
            {
                var t = Progress(rel, word.StartFrame, hand);
                return new WordDraw(t, 1, 0.5 * (1 - t), ActiveTint(anim, word, rel, basis), null);
            }
            case "wordPop":
            {
                var u = Linear(rel, word.StartFrame, hand);
                return new WordDraw(
                    Smoothstep(u), 0.6 + 0.4 * Overshoot(u), 0, ActiveTint(anim, word, rel, basis), null);
            }
            case "wordCycle":
            {
                var on = ActiveRamp(rel, word, hand);
                return new WordDraw(on, 1, 0, ActiveTint(anim, word, rel, basis), null);
            }
            case "highlightPop":
            {
                var on = ActiveRamp(rel, word, Math.Min(hand, 4));
                return new WordDraw(1, 1 + 0.15 * on, 0, Lerp(basis, highlight, on), null);
            }
            case "highlightBlock":
            {
                var on = ActiveRamp(rel, word, Math.Min(hand, 4));
                var bg = new TextStyle.RGBA
                {
                    R = highlight.R, G = highlight.G, B = highlight.B, A = highlight.A * on,
                };
                return new WordDraw(1, 1, 0, basis, bg);
            }
            default:
                return new WordDraw(1, 1, 0, basis, null);
        }
    }

    /// <summary>Mac activeTint — tints the active word only when the clip sets a highlight colour.</summary>
    private static TextStyle.RGBA ActiveTint(
        TextAnimation anim, WordTiming word, int rel, TextStyle.RGBA basis)
    {
        if (anim.Highlight is not { } hl) return basis;
        return Lerp(basis, hl, ActiveRamp(rel, word, Math.Max(1, anim.PerWordFrames)));
    }

    // ------------------------------------------------------------------ token timings

    /// <summary>
    /// One timing per token. Mac TextFrameRenderer.tokenTimings: zip when the counts agree, otherwise
    /// align transcript spans against the rendered tokens, falling back to an even split.
    /// </summary>
    public static WordTiming[] TokenTimings(
        string text,
        IReadOnlyList<(int Start, int Length)> tokens,
        IReadOnlyList<WordTiming>? words,
        int duration)
    {
        if (tokens.Count == 0) return [];
        if (words is null || words.Count == 0) return EvenTokenTimings(text, tokens, duration);

        if (words.Count == tokens.Count)
        {
            var zipped = new WordTiming[tokens.Count];
            for (var i = 0; i < tokens.Count; i++)
                zipped[i] = ClampedTiming(words[i], Token(text, tokens[i]), duration);
            return zipped;
        }

        return AlignedTokenTimings(text, tokens, words, duration)
            ?? EvenTokenTimings(text, tokens, duration);
    }

    private static WordTiming[] EvenTokenTimings(
        string text, IReadOnlyList<(int Start, int Length)> tokens, int duration)
    {
        var span = Math.Max(0, duration);
        var n = Math.Max(1, tokens.Count);
        var result = new WordTiming[tokens.Count];
        for (var i = 0; i < tokens.Count; i++)
        {
            result[i] = new WordTiming
            {
                Text = Token(text, tokens[i]),
                StartFrame = span * i / n,
                EndFrame = span * (i + 1) / n,
            };
        }
        return result;
    }

    /// <summary>
    /// Greedy two-pointer grouping on alphanumeric-normalised text, so a transcript run that split or
    /// merged relative to the rendered tokens still lands on the right words. Null when the two
    /// streams cannot be reconciled, which sends the caller to an even split.
    /// </summary>
    private static WordTiming[]? AlignedTokenTimings(
        string text,
        IReadOnlyList<(int Start, int Length)> tokens,
        IReadOnlyList<WordTiming> words,
        int duration)
    {
        var result = new List<WordTiming>(tokens.Count);
        var tokenIndex = 0;
        var wordIndex = 0;

        while (tokenIndex < tokens.Count && wordIndex < words.Count)
        {
            if (NextAlignedGroup(text, tokens, words, tokenIndex, wordIndex) is not { } group)
                return null;

            result.AddRange(TimingsForGroup(text, tokens, words, group, duration));
            tokenIndex = group.TokenEnd;
            wordIndex = group.WordEnd;
        }

        if (tokenIndex != tokens.Count || wordIndex != words.Count || result.Count != tokens.Count)
            return null;
        return [.. result];
    }

    private readonly record struct AlignGroup(int TokenStart, int TokenEnd, int WordStart, int WordEnd);

    private static AlignGroup? NextAlignedGroup(
        string text,
        IReadOnlyList<(int Start, int Length)> tokens,
        IReadOnlyList<WordTiming> words,
        int tokenStart,
        int wordStart)
    {
        var tokenEnd = tokenStart;
        var wordEnd = wordStart;
        var tokenText = new System.Text.StringBuilder();
        var wordText = new System.Text.StringBuilder();

        while (tokenEnd < tokens.Count || wordEnd < words.Count)
        {
            var takeToken =
                wordEnd >= words.Count
                || (tokenEnd < tokens.Count && tokenText.Length <= wordText.Length);

            if (takeToken)
            {
                tokenText.Append(NormalizedTimingText(Token(text, tokens[tokenEnd])));
                tokenEnd++;
            }
            else
            {
                wordText.Append(NormalizedTimingText(words[wordEnd].Text));
                wordEnd++;
            }

            if (tokenText.Length > 0
                && string.Equals(tokenText.ToString(), wordText.ToString(), StringComparison.Ordinal))
            {
                return new AlignGroup(tokenStart, tokenEnd, wordStart, wordEnd);
            }
        }
        return null;
    }

    private static WordTiming[] TimingsForGroup(
        string text,
        IReadOnlyList<(int Start, int Length)> tokens,
        IReadOnlyList<WordTiming> words,
        AlignGroup group,
        int duration)
    {
        var tokenCount = group.TokenEnd - group.TokenStart;
        var wordCount = group.WordEnd - group.WordStart;

        if (tokenCount == wordCount)
        {
            var zipped = new WordTiming[tokenCount];
            for (var i = 0; i < tokenCount; i++)
            {
                zipped[i] = ClampedTiming(
                    words[group.WordStart + i], Token(text, tokens[group.TokenStart + i]), duration);
            }
            return zipped;
        }

        // Counts differ inside the group: share the group's span across its tokens by position.
        var maxFrame = Math.Max(0, duration);
        var start = Math.Min(Math.Max(0, words[group.WordStart].StartFrame), maxFrame);
        var end = Math.Min(Math.Max(start, words[group.WordEnd - 1].EndFrame), maxFrame);
        var span = Math.Max(0, end - start);

        var shared = new WordTiming[tokenCount];
        for (var i = 0; i < tokenCount; i++)
        {
            shared[i] = new WordTiming
            {
                Text = Token(text, tokens[group.TokenStart + i]),
                StartFrame = start + span * i / tokenCount,
                EndFrame = i == tokenCount - 1 ? end : start + span * (i + 1) / tokenCount,
            };
        }
        return shared;
    }

    private static WordTiming ClampedTiming(WordTiming timing, string tokenText, int duration)
    {
        var maxFrame = Math.Max(0, duration);
        var start = Math.Min(Math.Max(0, timing.StartFrame), maxFrame);
        var end = Math.Min(Math.Max(start, timing.EndFrame), maxFrame);
        return new WordTiming { Text = tokenText, StartFrame = start, EndFrame = end };
    }

    private static string NormalizedTimingText(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var c in text)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    private static string Token(string text, (int Start, int Length) span)
        => text.Substring(span.Start, span.Length);

    /// <summary>Whitespace-delimited runs with their offsets. Mac TextFrameRenderer.words(in:).</summary>
    public static List<(int Start, int Length)> WordSpans(string text)
    {
        var spans = new List<(int Start, int Length)>();
        var i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (i >= text.Length) break;
            var start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
            spans.Add((start, i - start));
        }
        return spans;
    }

    // ------------------------------------------------------------------ easing

    private static string Typewriter(string text, int rel, int perCharFrames)
    {
        perCharFrames = Math.Max(1, perCharFrames);
        var n = Math.Min(text.Length, Math.Max(0, rel) / perCharFrames);
        return text[..n];
    }

    /// <summary>Eased 0→1 ramp across <paramref name="dur"/> frames from <paramref name="start"/>.</summary>
    public static double Progress(int rel, int start, int dur) => Smoothstep(Linear(rel, start, dur));

    /// <summary>Raw 0→1 ramp.</summary>
    public static double Linear(int rel, int start, int dur)
    {
        if (rel <= start) return 0;
        if (rel >= start + dur) return 1;
        return (rel - start) / (double)dur;
    }

    /// <summary>Mac Keyframe.swift smoothstep.</summary>
    public static double Smoothstep(double t) => t * t * (3 - 2 * t);

    /// <summary>Back-ease that overshoots past 1 before settling.</summary>
    public static double Overshoot(double t)
    {
        const double s = 1.70158;
        var p = t - 1;
        return 1 + (s + 1) * p * p * p + s * p * p;
    }

    /// <summary>
    /// 0 outside the word's span, ramping in and out inside it. The ramp is shortened to half the
    /// span so a fast word still reaches 1.
    /// </summary>
    public static double ActiveRamp(int rel, WordTiming word, int ramp)
    {
        if (rel < word.StartFrame || rel >= word.EndFrame) return 0;
        var span = Math.Max(1, word.EndFrame - word.StartFrame);
        if (span <= 1) return 1;
        var r = Math.Min(Math.Max(1, ramp), Math.Max(1, span / 2));
        var rampIn = Smoothstep(Math.Min(1, (rel - word.StartFrame) / (double)r));
        var rampOut = Smoothstep(Math.Min(1, (word.EndFrame - rel) / (double)r));
        return Math.Min(rampIn, rampOut);
    }

    public static TextStyle.RGBA Lerp(TextStyle.RGBA a, TextStyle.RGBA b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return new TextStyle.RGBA
        {
            R = a.R + (b.R - a.R) * t,
            G = a.G + (b.G - a.G) * t,
            B = a.B + (b.B - a.B) * t,
            A = a.A + (b.A - a.A) * t,
        };
    }
}
