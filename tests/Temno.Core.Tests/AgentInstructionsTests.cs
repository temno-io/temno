using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Temno.Core.Agent;
using Temno.Mcp;
using Xunit;

namespace Temno.Core.Tests;

public sealed class AgentInstructionsTests
{
    [Fact]
    public void InApp_IsWindowsHonest_NotTemnoSubscribe()
    {
        Assert.Contains("ggml-base.en", AgentInstructions.InApp, StringComparison.Ordinal);
        Assert.Contains(
            "inspect_media returns sentence segments in source seconds, or project frames when clipId is supplied; wordTimestamps adds timed words, subtitle assets return cues, and long results are capped and paged",
            AgentInstructions.InApp,
            StringComparison.Ordinal);
        Assert.DoesNotContain("inspect_media does not return a transcript", AgentInstructions.InApp, StringComparison.Ordinal);
        Assert.Contains("sign in for hosted credits", AgentInstructions.InApp, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Never silently switch from hosted 402", AgentInstructions.InApp, StringComparison.Ordinal);
        Assert.DoesNotContain("subscribe first", AgentInstructions.InApp, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InApp_NamesExpandedEditTools()
    {
        Assert.Contains("remove_silence", AgentInstructions.InApp, StringComparison.Ordinal);
        Assert.Contains("remove_words", AgentInstructions.InApp, StringComparison.Ordinal);
        Assert.Contains("add_captions", AgentInstructions.InApp, StringComparison.Ordinal);
        Assert.Contains("search_media", AgentInstructions.InApp, StringComparison.Ordinal);
        Assert.Contains("detect_beats", AgentInstructions.InApp, StringComparison.Ordinal);
        Assert.Contains("apply_layout", AgentInstructions.InApp, StringComparison.Ordinal);
    }

    [Fact]
    public void McpServer_AddsProjectNavigation()
    {
        Assert.Contains("\n\n# Projects\nmanage_project chooses", AgentInstructions.McpServer, StringComparison.Ordinal);
        Assert.DoesNotContain("# Projects", AgentInstructions.InApp, StringComparison.Ordinal);
    }

    // WP-308: one core, and each mode adds only its own paragraph at the end.
    [Fact]
    public void Every_mode_shares_the_core_and_ends_with_its_own_paragraph()
    {
        var core = Before(AgentInstructions.InApp, "\n\n# Your key\n");
        Assert.Equal(core, Before(AgentInstructions.McpServer, "\n\n# Projects\n"));
        Assert.Equal(core, Before(AgentInstructions.Hosted(AgentInstructions.QuoteTools), "\n\n# Hosted credits\n"));
        Assert.EndsWith("# Communication\n- One or two sentences; lead with the outcome. Never narrate steps. Never invent a reply when a tool or API key is missing — say so plainly.", core, StringComparison.Ordinal);
        Assert.DoesNotContain("# Hosted credits", AgentInstructions.InApp, StringComparison.Ordinal);
        Assert.DoesNotContain("# Your key", AgentInstructions.McpServer, StringComparison.Ordinal);
    }

    // A Gateway whose approved list lacks the quote tools gets a text that leaves out the lines naming them, and says so.
    [Fact]
    public void Hosted_without_the_quote_tools_drops_the_quote_first_lines_and_says_quotes_are_unavailable()
    {
        var withQuotes = AgentInstructions.Hosted(AgentInstructions.QuoteTools);
        var without = AgentInstructions.Hosted(["get_timeline", "generate_video", "quote_image"]);
        foreach (var name in AgentInstructions.QuoteTools)
        {
            Assert.DoesNotContain(name, without, StringComparison.Ordinal);
            Assert.Contains(name, withQuotes, StringComparison.Ordinal);
        }
        Assert.Contains("This chat cannot quote generations, so a model that list_models marks quoteRequired is unavailable here.", without, StringComparison.Ordinal);
        Assert.DoesNotContain("This chat cannot quote generations, so a model that list_models marks quoteRequired is unavailable here.", withQuotes, StringComparison.Ordinal);
        Assert.Contains("- Generation costs real money when a real provider is configured: hosted models spend the user's Temno credits", without, StringComparison.Ordinal);
        Assert.Contains("then wait for the user's yes in this conversation before you start one.", without, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_mode_is_composed_with_LF_only()
    {
        foreach (var text in new[] { AgentInstructions.InApp, AgentInstructions.McpServer, AgentInstructions.Hosted([]), AgentInstructions.Hosted(AgentInstructions.QuoteTools) })
            Assert.DoesNotContain('\r', text);
    }

    // Each mode names only tools that mode is given: the hosted turn's are the App's selection
    // (registry ∩ implemented, minus the project tools, WP-400) intersected with the Gateway's approved list.
    [Fact]
    public void Each_mode_names_only_tools_it_is_given()
    {
        var registry = McpToolRegistry.Tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var mcp = registry.Where(ToolExecutor.ImplementedTools.Contains).ToArray();
        var inApp = mcp.Where(n => !AgentToolGate.ProjectTools.Contains(n)).ToArray();
        Assert.Equal(5, mcp.Length - inApp.Length);
        var approved = JsonNode.Parse(File.ReadAllText(FindRepoFile("src/Temno.Gateway/Agent/approved-tools.json")))!.AsArray()
            .Select(t => t!["function"]!["name"]!.GetValue<string>()).ToArray();
        var hosted = new HostedAgentCatalog { Tools = approved }.SelectTools(inApp);

        Assert.Empty(Named(AgentInstructions.InApp, registry).Except(inApp));
        Assert.Empty(Named(AgentInstructions.McpServer, registry).Except(mcp));
        Assert.Empty(Named(AgentInstructions.Hosted(hosted), registry).Except(hosted));
        // WP-329: the approved list offers the quote tools, so hosted chat gets the quote-first lines.
        Assert.All(AgentInstructions.QuoteTools, name => Assert.Contains(name, hosted));
        var text = AgentInstructions.Hosted(hosted);
        Assert.Equal(AgentInstructions.Hosted(AgentInstructions.QuoteTools), text);
        Assert.Contains("- Hosted image models are quote-first, and a quote reserves nothing: call quote_image", text, StringComparison.Ordinal);
        Assert.Contains("Quote before proposing and show the credits; after the user's yes, call generate_video", text, StringComparison.Ordinal);
        Assert.Contains("- Hosted video, audio and upscale are also quote-first: call quote_media", text, StringComparison.Ordinal);
        Assert.Contains("- A quote is valid for two minutes: if the user's yes comes after it expired, quote again with the same inputs", text, StringComparison.Ordinal);
        Assert.DoesNotContain("This chat cannot quote generations", text, StringComparison.Ordinal);
    }

    // WP-329: the fixed MiniMax and Flux offers are gone, and no mode names MiniMax H3.
    [Fact]
    public void No_mode_describes_the_retired_fixed_offers()
    {
        foreach (var text in new[] { AgentInstructions.InApp, AgentInstructions.McpServer, AgentInstructions.Hosted([]), AgentInstructions.Hosted(AgentInstructions.QuoteTools) })
        {
            Assert.DoesNotContain("legacy", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("768P", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("H3", text, StringComparison.Ordinal);
        }
    }

    private static string Before(string text, string marker)
    {
        var at = text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(at > 0, marker);
        return text[..at];
    }

    private static IEnumerable<string> Named(string text, HashSet<string> registry) =>
        Regex.Matches(text, @"\b[a-z]+(?:_[a-z]+)+\b").Select(m => m.Value).Where(registry.Contains).Distinct();

    private static string FindRepoFile(string relativePath)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException($"{relativePath} was not found above {AppContext.BaseDirectory}.");
    }
}
