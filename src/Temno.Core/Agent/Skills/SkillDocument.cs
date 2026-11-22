using System.Text;

namespace Temno.Core.Agent.Skills;

/// <summary>
/// WP-309: one <c>SKILL.md</c> in the Agent Skills format (agentskills.io/specification), which Claude Code,
/// Codex, Cursor and Gemini CLI also read: YAML frontmatter with <c>name</c> and <c>description</c>, then
/// Markdown instructions. Temno reads the part of YAML that frontmatter uses (plain, quoted and block
/// scalars, one level of <c>metadata</c>) and refuses what it cannot read rather than guessing. Fields it
/// does not use, <c>allowed-tools</c> among them, are read past and named: a skill grants no tools.
/// Mac analog: <c>Agent/Skills/Skill.swift</c> (<c>SkillFrontmatter</c>), which checks only that the two fields exist.
/// </summary>
public sealed class SkillDocument
{
    public const int MaxNameLength = 64;
    public const int MaxDescriptionLength = 1024;
    public const int MaxCompatibilityLength = 500;

    /// <summary>The largest <c>SKILL.md</c> Temno reads, frontmatter included (D-4).</summary>
    public const int MaxFileBytes = 64 * 1024;

    public const string FileName = "SKILL.md";

    public required string Name { get; init; }
    public required string Description { get; init; }

    /// <summary>The instructions: everything after the frontmatter, trimmed.</summary>
    public required string Body { get; init; }

    public string? License { get; init; }
    public string? Compatibility { get; init; }
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();

    /// <summary>Frontmatter fields Temno does not use, in file order.</summary>
    public IReadOnlyList<string> IgnoredFields { get; init; } = [];

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>1-64 of a-z, 0-9 and hyphens, no hyphen at either end and none doubled.</summary>
    public static bool IsValidName(string? name) => NameProblem(name) is null;

    /// <summary>Why <paramref name="name"/> is not a skill name, or null when it is one.</summary>
    public static string? NameProblem(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return "SKILL.md needs a name.";
        if (name.Length > MaxNameLength)
            return $"The name is {name.Length} characters; the limit is {MaxNameLength}.";
        foreach (var c in name)
            if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '-'))
                return $"The name '{Shown(name)}' may use only lowercase letters a-z, digits and hyphens.";
        if (name[0] == '-' || name[^1] == '-')
            return $"The name '{Shown(name)}' cannot start or end with a hyphen.";
        if (name.Contains("--", StringComparison.Ordinal))
            return $"The name '{Shown(name)}' cannot have two hyphens in a row.";
        return null;
    }

    /// <summary>Parses raw <c>SKILL.md</c> bytes. <paramref name="folderName"/>, when given, must equal the name.</summary>
    public static SkillParseResult Parse(byte[] bytes, string? folderName)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length > MaxFileBytes)
            return SkillParseResult.Fail(TooLarge(bytes.Length));
        string text;
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return SkillParseResult.Fail("SKILL.md is not UTF-8 text.");
        }
        return Parse(text, folderName);
    }

    public static SkillParseResult Parse(string text, string? folderName)
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            return SkillParseResult.Success(ParseCore(text, folderName));
        }
        catch (SkillFormatException ex)
        {
            return SkillParseResult.Fail(ex.Message);
        }
    }

    public static string TooLarge(long bytes) =>
        $"SKILL.md is {(bytes + 1023) / 1024:N0} KB; the limit is {MaxFileBytes / 1024} KB.";

    /// <summary>A name, path or id as it may appear in a message: control characters dropped, shortened.</summary>
    public static string Shown(string value)
    {
        var builder = new StringBuilder();
        foreach (var c in value)
        {
            if (builder.Length >= 64)
                return builder.Append('\u2026').ToString();
            builder.Append(char.IsControl(c) ? '?' : c);
        }
        return builder.ToString();
    }

    private static SkillDocument ParseCore(string text, string? folderName)
    {
        if (text.Length > 0 && text[0] == '\uFEFF')
            text = text[1..];
        if (text.Contains('\0'))
            throw new SkillFormatException("SKILL.md contains a NUL character, so it is not a text file.");
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        if (lines.Length == 0 || lines[0].TrimEnd(' ', '\t') != "---")
            throw new SkillFormatException("SKILL.md must start with a --- line, then the name and description fields, then another --- line.");
        var close = -1;
        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].TrimEnd(' ', '\t') == "---")
            {
                close = i;
                break;
            }
        }
        if (close < 0)
            throw new SkillFormatException("The frontmatter has no closing --- line.");

        var fields = Frontmatter.Parse(lines.AsSpan(1, close - 1));
        var name = RequiredText(fields, "name", "SKILL.md needs a name.");
        if (NameProblem(name) is { } nameProblem)
            throw new SkillFormatException(nameProblem);
        if (folderName is not null && !string.Equals(name, folderName, StringComparison.Ordinal))
            throw new SkillFormatException(
                $"The name '{Shown(name)}' does not match its folder '{Shown(folderName)}'. A skill's name and folder must be the same.");

        var description = RequiredText(fields, "description", "SKILL.md needs a description.").Trim();
        if (description.Length == 0)
            throw new SkillFormatException("SKILL.md needs a description.");
        var descriptionLength = CountChars(description);
        if (descriptionLength > MaxDescriptionLength)
            throw new SkillFormatException(
                $"The description is {descriptionLength:N0} characters; the limit is {MaxDescriptionLength:N0}.");

        var compatibility = OptionalText(fields, "compatibility")?.Trim();
        if (compatibility is not null)
        {
            var length = CountChars(compatibility);
            if (length is 0 or > MaxCompatibilityLength)
                throw new SkillFormatException(
                    $"The compatibility field is {length:N0} characters; it must be 1 to {MaxCompatibilityLength}.");
        }

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        if (fields.TryGetValue("metadata", out var meta) && meta.Nested is { } nested)
            foreach (var (key, value) in Frontmatter.FlatMap(nested))
                metadata[key] = value;

        var body = string.Join('\n', lines.AsSpan(close + 1).ToArray()).Trim();
        return new SkillDocument
        {
            Name = name,
            Description = description,
            Body = body,
            License = OptionalText(fields, "license")?.Trim(),
            Compatibility = compatibility,
            Metadata = metadata,
            IgnoredFields = fields.Keys
                .Where(k => k is not ("name" or "description" or "license" or "compatibility" or "metadata"))
                .ToArray(),
        };
    }

    private static string RequiredText(IReadOnlyDictionary<string, Frontmatter.Value> fields, string key, string missing)
    {
        if (!fields.TryGetValue(key, out var value))
            throw new SkillFormatException(missing);
        return value.Text ?? throw new SkillFormatException($"The {key} field must be text, not a list or a map.");
    }

    private static string? OptionalText(IReadOnlyDictionary<string, Frontmatter.Value> fields, string key)
    {
        if (!fields.TryGetValue(key, out var value))
            return null;
        return value.Text ?? throw new SkillFormatException($"The {key} field must be text, not a list or a map.");
    }

    // The spec counts characters; a surrogate pair is one.
    private static int CountChars(string value) => value.EnumerateRunes().Count();
}

public sealed record SkillParseResult(SkillDocument? Document, string? Error)
{
    public bool Ok => Document is not null;

    public static SkillParseResult Success(SkillDocument document) => new(document, null);

    public static SkillParseResult Fail(string error) => new(null, error);
}

public sealed class SkillFormatException(string message) : Exception(message);

/// <summary>
/// The YAML subset skill frontmatter uses: top-level <c>key: value</c> lines whose value is a plain, single- or
/// double-quoted, or <c>|</c>/<c>&gt;</c> block scalar, or an indented block (a map or a list) kept as raw lines.
/// Anchors, aliases and tags are refused in fields Temno reads, so what Settings shows is what the agent gets.
/// </summary>
internal static class Frontmatter
{
    internal sealed record Value(string? Text, IReadOnlyList<string>? Nested, string? Unsupported);

    public static IReadOnlyDictionary<string, Value> Parse(ReadOnlySpan<string> lines)
    {
        var fields = new Dictionary<string, Value>(StringComparer.Ordinal);
        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];
            if (IsBlankOrComment(line))
            {
                i++;
                continue;
            }
            if (IsIndented(line))
                throw Error(i, "is indented where a field should start");
            var colon = line.IndexOf(':');
            if (colon <= 0)
                throw Error(i, "is not a 'field: value' line");
            var key = line[..colon].TrimEnd(' ', '\t');
            if (key.Length == 0 || !key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
                throw Error(i, $"has a field name Temno cannot read ('{SkillDocument.Shown(key)}')");
            var after = line[(colon + 1)..];
            if (after.Length > 0 && after[0] is not (' ' or '\t'))
                throw Error(i, "needs a space after the colon");
            var rest = after.Trim(' ', '\t');

            // The value's continuation: indented or blank lines, and a list written at the key's own indent.
            var j = i + 1;
            while (j < lines.Length
                   && (IsBlank(lines[j]) || IsIndented(lines[j]) || (rest.Length == 0 && IsListItem(lines[j]))))
                j++;
            var end = j;
            while (end > i + 1 && IsBlank(lines[end - 1]))
                end--;
            var continuation = lines[(i + 1)..end].ToArray();

            if (fields.ContainsKey(key))
                throw Error(i, $"sets '{key}' a second time");
            var value = ParseValue(rest, continuation, i);
            if (value.Unsupported is { } unsupported && key is "name" or "description" or "license" or "compatibility")
                throw Error(i, unsupported);
            fields[key] = value;
            i = j;
        }
        return fields;
    }

    /// <summary>One level of <c>key: value</c> pairs from a nested block, for metadata. Anything else is skipped.</summary>
    public static IEnumerable<KeyValuePair<string, string>> FlatMap(IReadOnlyList<string> nested)
    {
        foreach (var raw in nested)
        {
            var line = raw.Trim(' ', '\t');
            if (line.Length == 0 || line[0] == '#')
                continue;
            var colon = line.IndexOf(':');
            if (colon <= 0)
                continue;
            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' && value[^1] == '"' || value[0] == '\'' && value[^1] == '\''))
                value = value[1..^1];
            yield return new(key, value);
        }
    }

    private static Value ParseValue(string rest, string[] continuation, int line)
    {
        if (rest.Length == 0)
            return continuation.Length == 0 ? new Value("", null, null) : new Value(null, continuation, null);
        return rest[0] switch
        {
            '|' or '>' => new Value(BlockScalar(rest, continuation, line), null, null),
            '"' => new Value(Quoted(rest, continuation, line, '"'), null, null),
            '\'' => new Value(Quoted(rest, continuation, line, '\''), null, null),
            '[' or '{' => new Value(null, [rest, .. continuation], null),
            '&' or '*' or '!' or '%' or '@' or '`' => new Value(null, null, "uses YAML anchors, aliases or tags, which Temno does not read"),
            _ => new Value(Plain(rest, continuation), null, null),
        };
    }

    private static string Plain(string first, string[] continuation)
    {
        var builder = new StringBuilder(StripComment(first));
        var blank = false;
        foreach (var raw in continuation)
        {
            var part = StripComment(raw.Trim(' ', '\t'));
            if (part.Length == 0)
            {
                blank = true;
                continue;
            }
            builder.Append(blank ? '\n' : ' ').Append(part);
            blank = false;
        }
        return builder.ToString().Trim(' ', '\t');
    }

    // Callers pass a value already trimmed after whitespace, so a leading '#' starts a comment and the value
    // is empty, as YAML reads `description: # note` (T2 round 1, F5).
    private static string StripComment(string value)
    {
        if (value.StartsWith('#'))
            return "";
        for (var k = 1; k < value.Length; k++)
            if (value[k] == '#' && value[k - 1] is ' ' or '\t')
                return value[..(k - 1)].TrimEnd(' ', '\t');
        return value;
    }

    private static string BlockScalar(string header, string[] continuation, int line)
    {
        var literal = header[0] == '|';
        var chomp = ' ';
        var indent = 0;
        foreach (var c in StripComment(header)[1..].Trim(' ', '\t'))
        {
            if (c is '-' or '+' && chomp == ' ')
                chomp = c;
            else if (c is >= '1' and <= '9' && indent == 0)
                indent = c - '0';
            else
                throw Error(line, "has a block scalar header Temno cannot read");
        }
        if (indent == 0)
        {
            var first = continuation.FirstOrDefault(l => !IsBlank(l));
            indent = first is null ? 0 : first.Length - first.TrimStart(' ').Length;
        }

        var content = continuation.Select(l => l.Length >= indent && l[..indent].All(c => c == ' ') ? l[indent..] : l.TrimStart(' ')).ToList();
        string text;
        if (literal)
        {
            text = string.Join('\n', content);
        }
        else
        {
            var builder = new StringBuilder();
            var previousBlank = true;
            foreach (var part in content)
            {
                if (part.Length == 0)
                {
                    builder.Append('\n');
                    previousBlank = true;
                    continue;
                }
                if (!previousBlank && part[0] is not (' ' or '\t'))
                    builder.Append(' ');
                else if (!previousBlank)
                    builder.Append('\n');
                builder.Append(part);
                previousBlank = false;
            }
            text = builder.ToString();
        }
        text = text.TrimEnd('\n');
        return chomp switch
        {
            '-' => text,
            '+' => text + new string('\n', Math.Max(1, continuation.Reverse().TakeWhile(IsBlank).Count() + 1)),
            _ => text.Length == 0 ? "" : text + "\n",
        };
    }

    private static string Quoted(string first, string[] continuation, int line, char quote)
    {
        // Fold the raw lines first (a line break is a space, a blank line a newline), then read the quoted span.
        var raw = new StringBuilder(first);
        var blank = false;
        foreach (var l in continuation)
        {
            var part = l.Trim(' ', '\t');
            if (part.Length == 0)
            {
                blank = true;
                continue;
            }
            raw.Append(blank ? '\n' : ' ').Append(part);
            blank = false;
        }
        var s = raw.ToString();
        var value = new StringBuilder();
        var k = 1;
        for (; k < s.Length; k++)
        {
            var c = s[k];
            if (quote == '\'' && c == '\'')
            {
                if (k + 1 < s.Length && s[k + 1] == '\'')
                {
                    value.Append('\'');
                    k++;
                    continue;
                }
                break;
            }
            if (quote == '"' && c == '"')
                break;
            if (quote == '"' && c == '\\')
            {
                if (++k >= s.Length)
                    throw Error(line, "ends a quoted value with a lone backslash");
                k = Escape(s, k, value, line);
                continue;
            }
            value.Append(c);
        }
        if (k >= s.Length)
            throw Error(line, "has a quoted value with no closing quote");
        var tail = s[(k + 1)..].Trim(' ', '\t');
        if (tail.Length > 0 && tail[0] != '#')
            throw Error(line, "has text after a quoted value");
        return value.ToString();
    }

    private static int Escape(string s, int k, StringBuilder value, int line)
    {
        switch (s[k])
        {
            case '0': value.Append('\0'); return k;
            case 'a': value.Append('\a'); return k;
            case 'b': value.Append('\b'); return k;
            case 't' or '\t': value.Append('\t'); return k;
            case 'n': value.Append('\n'); return k;
            case 'v': value.Append('\v'); return k;
            case 'f': value.Append('\f'); return k;
            case 'r': value.Append('\r'); return k;
            case 'e': value.Append('\u001B'); return k;
            case ' ': value.Append(' '); return k;
            case '"': value.Append('"'); return k;
            case '/': value.Append('/'); return k;
            case '\\': value.Append('\\'); return k;
            case 'N': value.Append('\u0085'); return k;
            case '_': value.Append('\u00A0'); return k;
            case 'L': value.Append('\u2028'); return k;
            case 'P': value.Append('\u2029'); return k;
            case 'x': return Hex(s, k, 2, value, line);
            case 'u': return Hex(s, k, 4, value, line);
            case 'U': return Hex(s, k, 8, value, line);
            default: throw Error(line, $"has an escape Temno cannot read (\\{s[k]})");
        }
    }

    private static int Hex(string s, int k, int digits, StringBuilder value, int line)
    {
        if (k + digits >= s.Length
            || !int.TryParse(s.AsSpan(k + 1, digits), System.Globalization.NumberStyles.AllowHexSpecifier, null, out var code)
            || !Rune.IsValid(code))
            throw Error(line, "has a malformed \\x, \\u or \\U escape");
        value.Append(new Rune(code).ToString());
        return k + digits;
    }

    private static bool IsBlank(string line) => line.AsSpan().Trim(" \t").IsEmpty;

    private static bool IsBlankOrComment(string line) => IsBlank(line) || line.TrimStart(' ', '\t').StartsWith('#');

    private static bool IsIndented(string line) => line.Length > 0 && line[0] is ' ' or '\t';

    private static bool IsListItem(string line) => line == "-" || line.StartsWith("- ", StringComparison.Ordinal);

    // Frontmatter line numbers count from the file's first line, the opening ---.
    private static SkillFormatException Error(int index, string problem) =>
        new($"Line {index + 2} of SKILL.md {problem}.");
}
