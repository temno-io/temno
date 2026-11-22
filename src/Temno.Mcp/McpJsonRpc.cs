using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Temno.Mcp;

public class JsonRpcRequest
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    [JsonPropertyName("method")]
    public string Method { get; set; } = string.Empty;

    [JsonPropertyName("params")]
    public JsonNode? Params { get; set; }

    [JsonPropertyName("id")]
    public JsonElement? Id { get; set; }
}

public class JsonRpcResponse
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    [JsonPropertyName("id")]
    public JsonElement? Id { get; set; }

    [JsonPropertyName("result")]
    public object? Result { get; set; }

    [JsonPropertyName("error")]
    public JsonRpcError? Error { get; set; }
}

public class JsonRpcError
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    public object? Data { get; set; }
}

public class ToolCallResult
{
    [JsonPropertyName("content")]
    public List<TextContent> Content { get; set; } = [];

    [JsonPropertyName("isError")]
    public bool IsError { get; set; }
}

/// <summary>
/// One MCP content block. Despite the historical name this covers both block kinds:
/// text (<c>type="text"</c>, <c>Text</c> set) and image (<c>type="image"</c>, <c>Data</c> base64 +
/// <c>MimeType</c> set). Unused fields stay null and are omitted from the JSON.
/// </summary>
public class TextContent
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "text";

    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Data { get; set; }

    [JsonPropertyName("mimeType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MimeType { get; set; }

    public static TextContent FromText(string text) => new() { Type = "text", Text = text };

    public static TextContent FromPng(byte[] pngBytes) => new()
    {
        Type = "image",
        Text = null,
        Data = Convert.ToBase64String(pngBytes),
        MimeType = "image/png",
    };
}

public class Resource
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("uri")]
    public string Uri { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("mimeType")]
    public string MimeType { get; set; } = string.Empty;
}

public class ResourceListResult
{
    [JsonPropertyName("resources")]
    public List<Resource> Resources { get; set; } = [];
}

public class ResourceContent
{
    [JsonPropertyName("uri")]
    public string Uri { get; set; } = string.Empty;

    [JsonPropertyName("mimeType")]
    public string MimeType { get; set; } = string.Empty;

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;
}

public class ResourceReadResult
{
    [JsonPropertyName("contents")]
    public List<ResourceContent> Contents { get; set; } = [];
}
