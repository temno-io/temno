using System.Text.RegularExpressions;
using Temno.Mcp;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// Guards the rule that made `export_project` and `inspect_timeline` hang the whole app.
/// <para>
/// <see cref="ToolExecutor.Execute"/> marshals every tool that is not in
/// <c>OffDispatcherTools</c> onto the WinUI dispatcher and blocks the caller until it returns. A
/// handler that then blocks on async work (<c>GetAwaiter().GetResult()</c>) occupies the UI thread
/// for the whole operation — and, before <c>Temno.Media</c> was made <c>ConfigureAwait</c>-clean,
/// deadlocked it permanently, because the awaited continuation was posted to the very thread the
/// caller was blocking in.
/// </para>
/// <para>
/// These tests read the source rather than the compiled assembly because the property being checked
/// is a property of the code shape, not of runtime behaviour: they must fail the moment someone adds
/// a blocking call to a dispatcher-bound handler, not only when a user hits it.
/// </para>
/// </summary>
public sealed partial class McpDispatcherDisciplineTests
{
    /// <summary>
    /// Handlers that block on async work AND are knowingly still dispatcher-bound.
    /// <para>
    /// Each mutates project state from inside the async call (<c>CaptionGenerator.GenerateAsync</c>,
    /// <c>MulticamMemberSync.SyncAsync</c> and friends transcribe/analyse and then write clips in one
    /// step), so they cannot simply be moved off the dispatcher — the mutation would escape
    /// <c>ProjectHub.RunSerialized</c>. They no longer deadlock, but they do freeze the UI while
    /// running. Making them non-blocking means splitting analysis from mutation in the services
    /// beneath them; see docs/RENDER_AUDIT.md.
    /// </para>
    /// Do not add entries here to silence the test. Register the tool in
    /// <c>OffDispatcherTools</c> instead, and serialize only its short session reads.
    /// </summary>
    private static readonly Dictionary<string, string> KnownBlockingOnDispatcher = new(StringComparer.Ordinal)
    {
        ["AddCaptions"] = "add_captions",
        ["RemoveWords"] = "remove_words",
        ["SyncClips"] = "sync_clips",
        ["UpscaleMedia"] = "upscale_media",
        ["GenerateVideo"] = "generate_video",
        ["GenerateImage"] = "generate_image",
        ["GenerateAudio"] = "generate_audio",
        ["GetDefaultModel"] = "(generation helper — bounded by DefaultModelLookupTimeout)",
        ["ManageMulticamCreate"] = "manage_multicam",
    };

    /// <summary>Handlers whose blocking call is reached only from an off-dispatcher tool.</summary>
    private static readonly HashSet<string> ReachedOnlyOffDispatcher = new(StringComparer.Ordinal)
    {
        "InspectMedia",                             // inspect_media
        "ValidateAndProbeImportedMediaMetadata",    // import_media
        "DownloadImportSource",                     // import_media
        "RemoveSilence",                            // remove_silence
        "DetectBeats",                              // detect_beats
        "WaitForJob",                               // export_project
        "InspectTimeline",                          // inspect_timeline
        "MeasureScopes",                            // inspect_color
        "GetTranscript",                            // get_transcript
        "ListModels",                               // list_models
        "SearchMedia",                              // search_media (multi-line GetAwaiter().GetResult())
        "ListHostedImageModels",                    // list_models
        "QuoteImage",                               // quote_image
        "QuoteMedia",                               // quote_media
    };

    /// <summary>
    /// Awaits in the library roots that intentionally resume on the captured context.
    /// Keyed <c>FileName.cs::MethodName</c>; the value states why the capture is required.
    /// Empty means no library await may capture: the MCP tool surface calls these libraries
    /// synchronously from the WinUI thread, where a captured continuation is posted to a thread
    /// the caller is blocking in. Prefer fixing the await over adding an entry here; an entry
    /// whose await disappears fails <see cref="ContextBoundAwaitExceptions_AreAllLive"/>.
    /// </summary>
    private static readonly Dictionary<string, string> ContextBoundAwaitExceptions = new(StringComparer.Ordinal)
    {
    };

    /// <summary>
    /// Library roots whose awaits must never capture the calling context. WP-240 restored
    /// <c>Temno.Generation</c> alongside Core and Media (the name predates that addition).
    /// </summary>
    private static readonly string[] LibraryRoots = ["Temno.Core", "Temno.Media", "Temno.Generation"];

    [Fact]
    public void EveryBlockingHandler_IsEitherOffDispatcher_OrExplicitlyAccountedFor()
    {
        var unaccounted = new List<string>();

        foreach (var (file, source) in ToolExecutorSources())
        {
            var flat = ReadFlatSource(file, source);
            var blank = BlankNonCode(flat.Text, awaitsInStrings: null);
            var declarations = DeclaredMethods(blank);

            foreach (Match m in BlockingCall().Matches(blank))
            {
                var line = LineOf(flat, m.Index);
                var owner = EnclosingMethod(declarations, line);
                if (ReachedOnlyOffDispatcher.Contains(owner)) continue;
                if (KnownBlockingOnDispatcher.ContainsKey(owner)) continue;
                unaccounted.Add($"{file}:{line} in {owner}(): {m.Value.Trim()}");
            }
        }

        Assert.True(unaccounted.Count == 0,
            "These MCP handlers block on async work but are neither registered in "
            + "ToolExecutor.OffDispatcherTools nor listed as knowingly dispatcher-bound. Running on "
            + "the WinUI dispatcher, each freezes the app for the duration of the call:\n  "
            + string.Join("\n  ", unaccounted));
    }

    [Fact]
    public void HostedImageQuoteNetworkWork_RunsOffDispatcher()
        => Assert.Contains("quote_image", ToolExecutor.ToolsThatRunOffDispatcher);

    [Fact]
    public void ToolsKnownToBlock_AreRegisteredOffDispatcher()
    {
        // The blockers this test exists for. Losing any of these re-introduces a UI freeze.
        string[] required =
        [
            "export_project",     // renders every frame through ffmpeg
            "inspect_timeline",   // decodes + shells out per frame to encode a PNG
            "inspect_color",      // decodes a frame through ffmpeg
            "get_transcript",     // Whisper / cloud transcription
            "list_models",        // network round-trip
            "import_media",
            "inspect_media",
            "remove_silence",
            "detect_beats",
            "search_media",
        ];

        var registered = ToolExecutor.ToolsThatRunOffDispatcher;
        foreach (var tool in required)
            Assert.True(registered.Contains(tool), $"'{tool}' must run off the WinUI dispatcher.");
    }

    /// <summary>
    /// The library projects reached from those handlers must never resume on a captured context.
    /// CA2007-as-error in all three roots (see .editorconfig) is the primary guard; this test is
    /// the settings-independent tripwire — it still fires if the analyzer is ever silenced — with
    /// the reason in its failure message. The scan judges each whole await expression, however
    /// long or however many lines it spans, and the only way to exempt an await is an explicit
    /// <see cref="ContextBoundAwaitExceptions"/> entry, never a regex that happens not to match.
    /// Deliberate <c>.ConfigureAwait(false)</c> forms such as
    /// <c>await foreach (... .ConfigureAwait(false))</c> and
    /// <c>await using (x.ConfigureAwait(false))</c> pass like any configured await.
    /// </summary>
    [Fact]
    public void MediaAndCoreLibraries_NeverAwaitOnACapturedContext()
    {
        var (offenders, stringHits, _) = ScanLibraryAwaits();

        Assert.True(stringHits.Count == 0,
            "These string literals contain the word 'await' and are blanked before the scan, so an "
            + "await inside an interpolated hole would be invisible to it. Handle each explicitly:\n  "
            + string.Join("\n  ", stringHits));
        Assert.True(offenders.Count == 0,
            "These awaits capture the calling SynchronizationContext. The MCP tool surface calls "
            + "these libraries synchronously from the WinUI thread, where a captured continuation is "
            + "posted to a thread the caller is blocking in — a permanent app-wide hang:\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// Every <see cref="ContextBoundAwaitExceptions"/> entry must still match a live
    /// context-capturing await. A stale entry would silently bless whatever the next offender
    /// in that method is, so it fails instead.
    /// </summary>
    [Fact]
    public void ContextBoundAwaitExceptions_AreAllLive()
    {
        var (_, _, used) = ScanLibraryAwaits();
        var stale = ContextBoundAwaitExceptions.Keys
            .Where(k => !used.Contains(k))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        Assert.True(stale.Count == 0,
            "These context-capture exceptions no longer match any capturing await. Remove them:\n  "
            + string.Join("\n  ", stale));
    }

    private static (List<string> Offenders, List<string> StringHits, HashSet<string> UsedExceptions) ScanLibraryAwaits()
    {
        var offenders = new List<string>();
        var stringHits = new List<string>();
        var used = new HashSet<string>(StringComparer.Ordinal);

        foreach (var root in LibraryRoots)
        {
            var dir = LibraryPath(root);
            foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                    file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                    continue;

                var name = Path.GetFileName(file);
                var flat = ReadFlatSource(name, File.ReadAllText(file));
                var hits = new List<string>();
                var blank = BlankNonCode(flat.Text, hits);
                stringHits.AddRange(hits);
                var declarations = DeclaredMethods(blank);

                foreach (Match token in AwaitToken().Matches(blank))
                {
                    var line = LineOf(flat, token.Index);

                    var expression = ReadAwaitExpression(blank, token.Index + token.Length);
                    if (expression is null)
                    {
                        offenders.Add($"{name}:{line} in {EnclosingMethod(declarations, line)}(): <unterminated await expression>");
                        continue;
                    }
                    if (expression.Contains("ConfigureAwait", StringComparison.Ordinal)) continue;

                    var owner = EnclosingMethod(declarations, line);
                    var key = $"{name}::{owner}";
                    if (ContextBoundAwaitExceptions.ContainsKey(key))
                    {
                        used.Add(key);
                        continue;
                    }
                    var shown = expression.Trim().Replace('\n', ' ');
                    var entry = $"{name}:{line} in {owner}(): {shown[..Math.Min(160, shown.Length)]}";
                    if (!offenders.Contains(entry)) offenders.Add(entry);
                }
            }
        }

        return (offenders, stringHits, used);
    }

    /// Reads one awaited expression: from just after the <c>await</c> token to its terminating
    /// <c>;</c> at nesting depth zero, or to the bracket that closes an enclosure opened before
    /// the token — an await nested in call arguments or a <c>while</c>/<c>if</c> condition ends
    /// there (e.g. <c>while (await ch.WaitToReadAsync(ct).ConfigureAwait(false))</c>). Stopping
    /// at the enclosing close is sound: the awaited expression is grammatically complete by then.
    /// No length cap: a 400-character window once hid long statements from this check, and a
    /// split-line <c>.GetAwaiter().GetResult()</c> in SearchMedia hid from the blocking scan.
    /// Returns null only when the expression never terminates.
    /// </summary>
    private static string? ReadAwaitExpression(string blank, int start)
    {
        var depth = 0;
        for (var j = start; j < blank.Length; j++)
        {
            var ch = blank[j];
            if (ch is '(' or '[' or '{') depth++;
            else if (ch is ')' or ']' or '}')
            {
                if (depth == 0) return blank[start..j];
                depth--;
            }
            else if (ch == ';' && depth == 0) return blank[start..j];
        }
        return null;
    }

    private sealed record FlatSource(string File, string Text, int[] LineStarts);

    private static FlatSource ReadFlatSource(string file, string source)
    {
        var text = source.Replace("\r\n", "\n");
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
            if (text[i] == '\n' && i + 1 < text.Length)
                starts.Add(i + 1);
        return new FlatSource(file, text, starts.ToArray());
    }

    private static int LineOf(FlatSource flat, int offset)
    {
        var starts = flat.LineStarts;
        var lo = 0;
        var hi = starts.Length - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (starts[mid] <= offset) lo = mid;
            else hi = mid - 1;
        }
        return lo + 1;
    }

    /// <summary>
    /// Blanks comments, string and char literals in place (same length, newlines kept) so the
    /// shape scans see code only. Interpolated holes are blanked with the string; that is safe
    /// only because the scan fails on any string literal containing the word 'await' (reported
    /// through <paramref name="awaitsInStrings"/>; pass null for sources where prose may use it).
    /// </summary>
    private static string BlankNonCode(string text, List<string>? awaitsInStrings)
    {
        var chars = text.ToCharArray();
        var i = 0;
        while (i < chars.Length)
        {
            var c = chars[i];
            if (c == '/' && i + 1 < chars.Length && chars[i + 1] == '/')
            {
                while (i < chars.Length && chars[i] != '\n') chars[i++] = ' ';
            }
            else if (c == '/' && i + 1 < chars.Length && chars[i + 1] == '*')
            {
                chars[i++] = ' '; chars[i++] = ' ';
                while (i + 1 < chars.Length && !(chars[i] == '*' && chars[i + 1] == '/'))
                {
                    if (chars[i] != '\n') chars[i] = ' ';
                    i++;
                }
                if (i + 1 < chars.Length) { chars[i++] = ' '; chars[i++] = ' '; }
            }
            else if (c == '"')
            {
                i = BlankString(chars, i, awaitsInStrings, text);
            }
            else if (c == '\'')
            {
                chars[i++] = ' ';
                while (i < chars.Length && chars[i] != '\'' && chars[i] != '\n')
                {
                    if (chars[i] == '\\' && i + 1 < chars.Length) chars[i++] = ' ';
                    chars[i++] = ' ';
                }
                if (i < chars.Length && chars[i] == '\'') chars[i++] = ' ';
            }
            else
            {
                i++;
            }
        }
        return new string(chars);
    }

    private static int BlankString(char[] chars, int quote, List<string>? awaitsInStrings, string text)
    {
        var verbatim = quote - 1 >= 0 && chars[quote - 1] == '@';
        var content = new System.Text.StringBuilder();
        chars[quote] = ' ';
        var i = quote + 1;
        while (i < chars.Length)
        {
            if (!verbatim && chars[i] == '\\' && i + 1 < chars.Length)
            {
                content.Append(chars[i]); content.Append(chars[i + 1]);
                chars[i++] = ' '; chars[i++] = ' ';
            }
            else if (verbatim && chars[i] == '"' && i + 1 < chars.Length && chars[i + 1] == '"')
            {
                content.Append("\"\"");
                chars[i++] = ' '; chars[i++] = ' ';
            }
            else if (chars[i] == '"')
            {
                chars[i++] = ' ';
                break;
            }
            else if (!verbatim && chars[i] == '\n')
            {
                break; // Unterminated: a regular string cannot span lines; resume scanning.
            }
            else
            {
                content.Append(chars[i]);
                if (chars[i] != '\n') chars[i] = ' ';
                i++;
            }
        }
        if (awaitsInStrings is not null && AwaitToken().IsMatch(content.ToString()))
        {
            var line = 1;
            for (var k = 0; k < quote; k++) if (text[k] == '\n') line++;
            awaitsInStrings.Add($"line {line}: \"{content.ToString().Trim()[..Math.Min(80, content.ToString().Trim().Length)]}...\"");
        }
        return i;
    }

    private static List<(int Line, string Name)> DeclaredMethods(string blank)
    {
        var lines = blank.Split('\n');
        var decls = new List<(int Line, string Name)>();
        for (var i = 0; i < lines.Length; i++)
        {
            var m = MethodDeclaration().Match(lines[i]);
            if (m.Success) decls.Add((i + 1, m.Groups[1].Value));
        }
        return decls;
    }

    private static string EnclosingMethod(List<(int Line, string Name)> declarations, int line)
    {
        string? current = null;
        foreach (var (declLine, name) in declarations)
        {
            if (declLine <= line) current = name;
            else break;
        }
        return current ?? "<unknown>";
    }

    private static IEnumerable<(string File, string Source)> ToolExecutorSources()
    {
        var root = LibraryPath("Temno.Mcp");
        foreach (var file in Directory.EnumerateFiles(root, "ToolExecutor*.cs", SearchOption.TopDirectoryOnly))
            yield return (Path.GetFileName(file), File.ReadAllText(file));
    }

    private static string LibraryPath(string project)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", project)))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", project);
    }

    [GeneratedRegex(@"^\s*(?:private|public|internal|protected)\s+(?:static\s+)?(?:async\s+)?[\w<>,\?\[\]\.\s]+?\s+([A-Z][A-Za-z0-9_]*)\s*\(")]
    private static partial Regex MethodDeclaration();

    /// <summary>
    /// Every way to block a thread on async work, with nesting whitespace tolerated so a call
    /// split across lines still matches: <c>.GetAwaiter().GetResult()</c>, <c>.Wait(...)</c>,
    /// <c>.Result</c>, and <c>Task.WaitAll/WaitAny</c>. Runs on comment- and string-blanked
    /// source, so prose that names these calls is not mistaken for one.
    /// </summary>
    [GeneratedRegex(@"\.GetAwaiter\s*\(\s*\)\s*\.GetResult\s*\(|\.Wait\s*\(|\.Result\b|Task\.WaitAll\s*\(|Task\.WaitAny\s*\(")]
    private static partial Regex BlockingCall();

    [GeneratedRegex(@"(?<![\w@])await(?![\w])")]
    private static partial Regex AwaitToken();
}
