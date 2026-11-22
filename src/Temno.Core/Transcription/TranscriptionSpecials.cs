namespace Temno.Core.Transcription;

/// <summary>
/// Whisper / decoder control tokens that are not spoken words. Silence markers such as
/// <c>[BLANK_AUDIO]</c> must not become caption clips.
/// </summary>
public static class TranscriptionSpecials
{
    public static bool IsSpecialToken(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return true;
        var t = text.Trim();
        if (t.StartsWith("<|", StringComparison.Ordinal) && t.EndsWith("|>", StringComparison.Ordinal))
            return true;
        if (t.StartsWith("[_", StringComparison.Ordinal) && t.EndsWith("_]", StringComparison.Ordinal))
            return true;
        if (t.Length >= 2 && t[0] == '[' && t[^1] == ']')
            return true;
        return false;
    }

    public static string StripSpecialTokens(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";
        var spoken = text
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Where(part => !IsSpecialToken(part));
        return string.Join(" ", spoken);
    }
}
