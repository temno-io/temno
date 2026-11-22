using System.Globalization;
using System.Text;

namespace Temno.Core.Captions;

/// <summary>
/// SubRip (.srt) parse/serialize. No Mac counterpart exists; the format
/// contract is standard SRT ("index / HH:MM:SS,mmm --> HH:MM:SS,mmm / text").
/// Parsing is tolerant: CRLF/CR and BOM accepted, '.' accepted as millisecond
/// separator, malformed cue blocks are skipped and reported via Warnings.
/// </summary>
public static class SrtParser
{
    public static CaptionParseResult Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var result = new CaptionParseResult();
        foreach (var block in CueBlocks.Split(content))
            ParseBlock(block, result);
        return result;
    }

    /// <summary>
    /// Cue numbers come from CaptionEntry.Index when positive, else the
    /// 1-based list position. Output uses '\n' line endings and a blank line
    /// after every cue.
    /// </summary>
    public static string Serialize(IReadOnlyList<CaptionEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var sb = new StringBuilder();
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            sb.Append(entry.Index > 0 ? entry.Index : i + 1).Append('\n');
            sb.Append(CaptionTimecode.Format(entry.StartSeconds, ','))
                .Append(" --> ")
                .Append(CaptionTimecode.Format(entry.EndSeconds, ','))
                .Append('\n');
            sb.Append(NormalizeNewlines(entry.Text)).Append('\n');
            sb.Append('\n');
        }
        return sb.ToString();
    }

    internal static string NormalizeNewlines(string text)
        => text.Replace("\r\n", "\n").Replace('\r', '\n');

    private static void ParseBlock(CueBlocks.Block block, CaptionParseResult result)
    {
        var timingIndex = block.Lines.FindIndex(
            l => l.Contains("-->", StringComparison.Ordinal));
        if (timingIndex < 0)
        {
            result.Warnings.Add(
                $"Line {block.LineNumber}: skipped cue block without a '-->' timing line.");
            return;
        }

        if (!CueBlocks.TryParseTimingLine(block.Lines[timingIndex], out var start, out var end))
        {
            result.Warnings.Add(
                $"Line {block.LineNumber + timingIndex}: skipped cue with unparsable timing " +
                $"line '{block.Lines[timingIndex].Trim()}'.");
            return;
        }

        if (end <= start)
        {
            result.Warnings.Add(
                $"Line {block.LineNumber + timingIndex}: skipped cue whose end time is not " +
                "after its start time.");
            return;
        }

        var index = 0;
        if (timingIndex == 1)
        {
            if (!int.TryParse(
                    block.Lines[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture,
                    out index))
            {
                result.Warnings.Add(
                    $"Line {block.LineNumber}: cue number '{block.Lines[0].Trim()}' is not an " +
                    "integer; renumbered sequentially.");
                index = 0;
            }
        }
        else if (timingIndex > 1)
        {
            result.Warnings.Add(
                $"Line {block.LineNumber}: ignored {timingIndex - 1} unexpected line(s) before " +
                "the cue timing line.");
        }

        var textLines = block.Lines.Skip(timingIndex + 1).ToList();
        if (textLines.Count == 0)
        {
            result.Warnings.Add(
                $"Line {block.LineNumber + timingIndex}: skipped cue with no text.");
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
