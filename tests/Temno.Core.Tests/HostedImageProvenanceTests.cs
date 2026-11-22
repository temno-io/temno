using System.Text.Json;
using Temno.Core.Models;
using Xunit;

namespace Temno.Core.Tests;

public sealed class HostedImageProvenanceTests
{
    [Fact]
    public void Cancellation_and_quote_identity_survive_the_project_wire_format()
    {
        const string json = """
            {"prompt":"edit","model":"muse-image","operation":"edit_image",
             "settings":{"count":"2"},"sourceAssetIds":["source"],"quoteId":"quote",
             "submitKey":"11111111-1111-4111-8111-111111111111","quotedCredits":8,"cancelRequested":true}
            """;
        var saved = JsonSerializer.Deserialize<GenerationInput>(json, JsonOptions.ForProject)!;
        Assert.True(saved.CancelRequested);
        Assert.Equal("quote", saved.QuoteId);
        Assert.Equal("2", saved.Settings!["count"]);
        var output = JsonSerializer.Serialize(saved, JsonOptions.ForProject);
        using var doc = JsonDocument.Parse(output);
        Assert.True(doc.RootElement.GetProperty("cancelRequested").GetBoolean());
        Assert.Equal("11111111-1111-4111-8111-111111111111", doc.RootElement.GetProperty("submitKey").GetString());
        var legacy = JsonSerializer.Deserialize<GenerationInput>("{\"prompt\":\"old\",\"model\":\"flux-schnell\"}", JsonOptions.ForProject)!;
        Assert.Null(legacy.Operation);
        Assert.Null(legacy.CancelRequested);
    }
}
