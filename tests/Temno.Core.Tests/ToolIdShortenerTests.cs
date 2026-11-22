using Temno.Mcp;
using Xunit;

namespace Temno.Core.Tests;

public sealed class ToolIdShortenerTests
{
    [Fact]
    public void ShortIdMap_UsesFloorWhenIdsAreDistinct()
    {
        var a = "AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA";
        var b = "BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB";
        var map = ToolIdShortener.ShortIdMap(new HashSet<string> { a, b });
        Assert.Equal(a[..8], map[a]);
        Assert.Equal(b[..8], map[b]);
    }

    [Fact]
    public void ShortIdMap_LengthensWhenPrefixesCollide()
    {
        var a = "AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA";
        var b = "AAAAAAAA-BBBB-BBBB-BBBB-BBBBBBBBBBBB";
        var map = ToolIdShortener.ShortIdMap(new HashSet<string> { a, b });
        Assert.True(map[a].Length > 8);
        Assert.True(map[b].Length > 8);
        Assert.NotEqual(map[a], map[b]);
        Assert.StartsWith(map[a], a);
        Assert.StartsWith(map[b], b);
    }

    [Fact]
    public void ExpandOne_ResolvesUniquePrefix()
    {
        var full = "AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA";
        var universe = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { full };
        Assert.Equal(full, ToolIdShortener.ExpandOne(full[..8], universe));
    }

    [Fact]
    public void ExpandOne_ThrowsWhenAmbiguous()
    {
        var universe = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA",
            "AAAAAAAA-BBBB-BBBB-BBBB-BBBBBBBBBBBB",
        };
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ToolIdShortener.ExpandOne("AAAAAAAA", universe));
        Assert.Contains("Ambiguous", ex.Message);
    }

    [Fact]
    public void ExpandOne_LeavesUnknownPrefixUntouched()
    {
        var universe = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA",
        };
        Assert.Equal("CCCCCCCC", ToolIdShortener.ExpandOne("CCCCCCCC", universe));
    }

    [Fact]
    public void Shorten_RewritesUuidsInTextBlocksOnly()
    {
        var full = "AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA";
        var result = new ToolCallResult
        {
            Content =
            [
                TextContent.FromText($"{{\n  \"id\": \"{full}\"\n}}"),
                new TextContent { Type = "image", Data = full, MimeType = "image/png", Text = null },
            ],
        };
        ToolIdShortener.Shorten(result, new HashSet<string> { full });
        Assert.Contains(full[..8], result.Content[0].Text);
        Assert.DoesNotContain(full, result.Content[0].Text);
        Assert.Equal(full, result.Content[1].Data);
    }
}
