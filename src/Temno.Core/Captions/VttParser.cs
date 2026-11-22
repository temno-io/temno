using System.Globalization;
using System.Text;

namespace Temno.Core.Captions;

/// <summary>
/// WebVTT (.vtt) parse/serialize. No Mac counterpart exists; the format
/// contract is standard WebVTT. Parsing is tolerant: CRLF/CR and BOM accepted,
/// ',' accepted as millisecond separator, optional hours accepted, cue
/// settings after the end timestamp ignored, NOTE/STYLE/REGION blocks skipped,
/// malformed cue blocks skipped and reported via Warnings.
/// </summary>
public static class VttParser
{
    public static CaptionParseResult Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var result = new CaptionParseResult();

        var blocks = CueBlocks.Split(content);
        var hasHeader = blocks.Count > 0
            && blocks[0].Lines[0].StartsWith("WEBVTT", StringComparison.Ordinal);
        if (!hasHeader)
            result.Warnings.Add("Missing WEBVTT header; attempting tolerant parse.");

        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            var lines = block.Lines;
            var lineNumber = block.LineNumber;

            if (i == 0 && hasHeader)
            {
                // Header block: "WEBVTT" plus optional metadata lines. A cue
                // jammed into the same block (missing blank line) still parses.
                lines = lines.Skip(1).ToList();
                lineNumber++;
                if (!lines.Any(l => l.Contains("-->", StringComparison.Ordinal)))
                    continue;
            }

            var keyword = lines[0].TrimStart();
            if (keyword.StartsWith("NOTE", StringComparison.Ordinal)
                || keyword.StartsWith("STYLE", StringComparison.Ordinal)
                || keyword.StartsWith("REGION", StringComparison.Ordinal))
            {
                continue;
            }

            ParseCueBlock(lines, lineNumber, result);
        }
        return result;
    }

    /// <summary>
    /// Emits a numeric cue identifier line per cue (CaptionEntry.Index when
    /// positive, else the 1-based position) so SRT round-trips keep numbering.
    /// Output uses '\n' line endings.
    /// </summary>
    public static string Serialize(IReadOnlyList<CaptionEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var sb = new StringBuilder("WEBVTT\n\n");
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            sb.Append(entry.Index > 0 ? entry.Index : i + 1).Append('\n');
            sb.Append(CaptionTimecode.Format(entry.StartSeconds, '.'))
                .Append(" --> ")
                .Append(CaptionTimecode.Format(entry.EndSeconds, '.'))
                .Append('\n');
            sb.Append(SrtParser.NormalizeNewlines(entry.Text)).Append('\n');
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static void ParseCueBlock(List<string> lines, int lineNumber, CaptionParseResult result)
    {
        var timingIndex = lines.FindIndex(l => l.Contains("-->", StringComparison.Ordinal));
        if (timingIndex < 0)
        {
            result.Warnings.Add(
                $"Line {lineNumber}: skipped cue block without a '-->' timing line.");
            return;
        }

        if (!CueBlocks.TryParseTimingLine(lines[timingIndex], out var start, out var end))
        {
            result.Warnings.Add(
                $"Line {lineNumber + timingIndex}: skipped cue with unparsable timing line " +
                $"'{lines[timingIndex].Trim()}'.");
            return;
        }

        if (end <= start)
        {
            result.Warnings.Add(
                $"Line {lineNumber + timingIndex}: skipped cue whose end time is not after " +
                "its start time.");
            return;
        }

        // A cue identifier is any single line before the timing line; only a
        // numeric one maps onto CaptionEntry.Index (non-numeric ids are legal
        // VTT and dropped silently).
        var index = 0;
        if (timingIndex == 1)
        {
            _ = int.TryParse(
                lines[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out index);
        }
        else if (timingIndex > 1)
        {
            result.Warnings.Add(
                $"Line {lineNumber}: ignored {timingIndex - 1} unexpected line(s) before the " +
                "cue timing line.");
        }

        var textLines = lines.Skip(timingIndex + 1).ToList();
        if (textLines.Count == 0)
        {
            result.Warnings.Add(
                $"Line {lineNumber + timingIndex}: skipped cue with no text.");
            return;
        }

        result.Entries.Add(new CaptionEntry
        {
            Index = index > 0 ? index : result.Entries.Count + 1,
            StartSeconds = start,
            EndSeconds = end,
            Text = string.Join('\n', textLines),
        });
    }
}
