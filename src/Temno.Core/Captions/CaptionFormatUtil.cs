using System.Globalization;

namespace Temno.Core.Captions;

/// <summary>Timestamp parse/format shared by SrtParser and VttParser.</summary>
internal static class CaptionTimecode
{
    /// <summary>
    /// Accepts "HH:MM:SS,mmm", "HH:MM:SS.mmm" and "MM:SS.mmm" (hours optional,
    /// either millisecond separator, tolerant across both formats). Fraction
    /// digits are decimal fractions of a second (".5" == 500 ms).
    /// </summary>
    public static bool TryParse(string token, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(token))
            return false;

        var parts = token.Trim().Split(':');
        if (parts.Length is < 2 or > 3)
            return false;

        var secondsPart = parts[^1];
        double fraction = 0;
        var separatorIndex = secondsPart.IndexOfAny([',', '.']);
        if (separatorIndex >= 0)
        {
            var fractionDigits = secondsPart[(separatorIndex + 1)..];
            secondsPart = secondsPart[..separatorIndex];
            if (fractionDigits.Length == 0 || !AllAsciiDigits(fractionDigits))
                return false;
            fraction = double.Parse(fractionDigits, CultureInfo.InvariantCulture)
                / Math.Pow(10, fractionDigits.Length);
        }

        if (!TryParseComponent(secondsPart, out var wholeSeconds) || wholeSeconds > 59)
            return false;
        if (!TryParseComponent(parts[^2], out var minutes) || minutes > 59)
            return false;

        var hours = 0;
        if (parts.Length == 3 && !TryParseComponent(parts[0], out hours))
            return false;

        seconds = hours * 3600.0 + minutes * 60.0 + wholeSeconds + fraction;
        return true;
    }

    public static string Format(double seconds, char millisecondSeparator)
    {
        var totalMs = (long)Math.Round(seconds * 1000.0, MidpointRounding.AwayFromZero);
        if (totalMs < 0)
            totalMs = 0;
        var h = totalMs / 3_600_000;
        var m = totalMs / 60_000 % 60;
        var s = totalMs / 1000 % 60;
        var ms = totalMs % 1000;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{h:00}:{m:00}:{s:00}{millisecondSeparator}{ms:000}");
    }

    private static bool TryParseComponent(string text, out int value)
        => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    private static bool AllAsciiDigits(string text)
    {
        foreach (var c in text)
        {
            if (c is < '0' or > '9')
                return false;
        }
        return true;
    }
}

/// <summary>Cue-block splitting shared by SrtParser and VttParser.</summary>
internal static class CueBlocks
{
    public sealed record Block(int LineNumber, List<string> Lines);

    /// <summary>
    /// Splits content into blocks of consecutive non-blank lines, tolerating
    /// CRLF/CR line endings and a UTF-8 BOM. LineNumber is the 1-based line of
    /// the block's first line (for warnings).
    /// </summary>
    public static List<Block> Split(string content)
    {
        var blocks = new List<Block>();
        var lines = content
            .TrimStart('\uFEFF')
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n');

        List<string>? current = null;
        var currentStart = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                if (current is not null)
                {
                    blocks.Add(new Block(currentStart, current));
                    current = null;
                }
                continue;
            }

            if (current is null)
            {
                current = [];
                currentStart = i + 1;
            }
            current.Add(lines[i]);
        }
        if (current is not null)
            blocks.Add(new Block(currentStart, current));
        return blocks;
    }

    /// <summary>
    /// Parses "start --> end [settings...]"; trailing tokens after each
    /// timestamp (VTT cue settings, legacy SRT coordinates) are ignored.
    /// </summary>
    public static bool TryParseTimingLine(string line, out double startSeconds, out double endSeconds)
    {
        startSeconds = 0;
        endSeconds = 0;
        var arrow = line.IndexOf("-->", StringComparison.Ordinal);
        if (arrow < 0)
            return false;
        var left = FirstToken(line[..arrow]);
        var right = FirstToken(line[(arrow + 3)..]);
        return CaptionTimecode.TryParse(left, out startSeconds)
            && CaptionTimecode.TryParse(right, out endSeconds);
    }

    private static string FirstToken(string text)
    {
        var trimmed = text.Trim();
        var space = trimmed.IndexOfAny([' ', '\t']);
        return space < 0 ? trimmed : trimmed[..space];
    }
}
