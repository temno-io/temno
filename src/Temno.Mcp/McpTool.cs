using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Temno.Mcp;

public class McpTool
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("inputSchema")]
    public JsonObject InputSchema { get; set; } = [];
}
