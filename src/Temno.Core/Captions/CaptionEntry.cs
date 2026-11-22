namespace Temno.Core.Captions;

/// <summary>
/// A single subtitle cue in the caption-file domain. Times are SECONDS
/// (source/file domain per the repo units convention); conversion to timeline
/// frames happens only in CaptionImporter. No Mac counterpart exists (the Mac
/// app has no SRT/VTT support; captions live purely as text clips).
/// </summary>
public sealed class CaptionEntry
{
    /// <summary>
    /// 1-based cue number (SRT index / numeric VTT identifier). Parsers assign
    /// the 1-based file position when the source file carries no usable number.
    /// </summary>
    public int Index { get; set; }

    public double StartSeconds { get; set; }

    public double EndSeconds { get; set; }

    /// <summary>Cue text; internal line breaks are normalized to '\n'.</summary>
    public string Text { get; set; } = "";
}

/// <summary>Parse output: cues that survived plus notes about skipped/repaired blocks.</summary>
public sealed class CaptionParseResult
{
    public List<CaptionEntry> Entries { get; } = [];

    public List<string> Warnings { get; } = [];
}
