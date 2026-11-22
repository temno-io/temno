using System.Text;
using System.Text.RegularExpressions;

namespace Temno.Core.Captions;

/// <summary>
/// Reads an SRT/WebVTT file into caption cues. Mac: Captions/SubtitleFileParser.swift — UTF-8 with a
/// Latin-1 fallback, markup stripped from cue text, and SRT/WebVTT only (ASS/SSA is rejected on both
/// platforms).
///
/// Divergence from Mac, deliberate: Mac requires a WEBVTT header and fails without it; the Windows
/// <see cref="VttParser"/> parses tolerantly and records a warning instead, which WP-115 keeps
/// because the parser and its tests predate this package.
/// </summary>
public static class SubtitleFile
{
    public static readonly IReadOnlyList<string> SupportedExtensions = [".srt", ".vtt"];

    public static bool IsSubtitleExtension(string? extension)
        => extension is not null
            && SupportedExtensions.Contains(extension.Trim().ToLowerInvariant(), StringComparer.Ordinal);

    /// <summary>Cues from a subtitle file on disk.</summary>
    /// <exception cref="InvalidDataException">
    /// Unsupported extension, unreadable bytes, or a file that yields no cues.
    /// </exception>
    public static CaptionParseResult Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
            throw new InvalidDataException($"subtitle file not found: {Path.GetFileName(path)}");
        return Parse(File.ReadAllBytes(path), Path.GetExtension(path));
    }

    /// <exception cref="InvalidDataException">
    /// Unsupported extension or a file that yields no cues.
    /// </exception>
    public static CaptionParseResult Parse(byte[] bytes, string? extension)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var ext = extension?.Trim().ToLowerInvariant() ?? "";
        if (!IsSubtitleExtension(ext))
            throw new InvalidDataException($"'{ext}' subtitles are not supported. Use SRT or WebVTT.");

        var text = DecodeText(bytes);
        var parsed = ext == ".vtt" ? VttParser.Parse(text) : SrtParser.Parse(text);

        var result = new CaptionParseResult();
        result.Warnings.AddRange(parsed.Warnings);
        foreach (var entry in parsed.Entries)
        {
            var stripped = StripMarkup(entry.Text);
            if (stripped.Length == 0) continue;
            entry.Text = stripped;
            result.Entries.Add(entry);
        }

        if (result.Entries.Count == 0)
            throw new InvalidDataException("subtitle file contains no cues.");
        return result;
    }

    /// <summary>Source-domain seconds covered by the cues, used as the library asset duration.</summary>
    public static double DurationSeconds(IReadOnlyList<CaptionEntry> entries)
        => entries.Count == 0 ? 0 : Math.Max(0, entries.Max(e => e.EndSeconds));

    /// <summary>UTF-8 (BOM tolerated) with a Latin-1 fallback, matching Mac SubtitleFileParser.</summary>
    internal static string DecodeText(byte[] bytes)
    {
        try
        {
            var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            var text = strict.GetString(bytes);
            return text.Length > 0 && text[0] == '﻿' ? text[1..] : text;
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    private static readonly Regex MarkupPattern = new(@"<[^<>]*>|\{[^{}]*\}", RegexOptions.Compiled);

    /// <summary>Drops WebVTT/SRT inline tags and SSA-style override blocks from cue text.</summary>
    internal static string StripMarkup(string text)
    {
        if (string.IsNullOrEmpty(text) || (!text.Contains('<') && !text.Contains('{')))
            return text.Trim();

        var stripped = MarkupPattern.Replace(text, "");
        return string.Join(
            '\n',
            stripped.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0));
    }
}
