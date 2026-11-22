using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Temno.Core.IO;

namespace Temno.Core.Agent;

public sealed class AnthropicToolSchema
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public JsonNode? InputSchema { get; init; }

    public static IReadOnlyList<AnthropicToolSchema> FromCatalog(
        IEnumerable<(string Name, string Description, JsonNode? InputSchema)> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        return tools.Select(t => new AnthropicToolSchema
        {
            Name = t.Name,
            Description = t.Description,
            InputSchema = t.InputSchema,
        }).ToList();
    }
}

public sealed class AnthropicTurn
{
    public required string StopReason { get; init; }
    public required IReadOnlyList<AgentContentBlock> Blocks { get; init; }
}

public enum AnthropicClientErrorKind
{
    MissingApiKey,
    HttpError,
    InvalidResponse,
}

public sealed class AnthropicClientException : Exception
{
    public AnthropicClientErrorKind Kind { get; }
    public int? StatusCode { get; }

    public AnthropicClientException(AnthropicClientErrorKind kind, string message, int? statusCode = null)
        : base(message)
    {
        Kind = kind;
        StatusCode = statusCode;
    }
}

public static class AgentSetup
{
    public const string MissingKeyError = "No Anthropic API key is set.";

    public const string MissingKeyCta =
        "Sign in for hosted chat, add an Anthropic API key in Settings, or use the Tools console.";
}

public sealed class AnthropicClient
{
    public const string ApiVersion = "2023-06-01";
    public const string DefaultModel = AgentPrefs.DefaultModel;
    public const string DefaultBaseUrl = AgentPrefs.DefaultBaseUrl;

    private readonly string _apiKey;
    private readonly string _baseUrl;
    private readonly string _model;
    private readonly int _maxTokens;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public AnthropicClient(
        string? apiKey,
        string? baseUrl = null,
        string? model = null,
        HttpClient? httpClient = null,
        int maxTokens = 8192)
    {
        _apiKey = apiKey?.Trim() ?? "";
        _baseUrl = string.IsNullOrWhiteSpace(baseUrl)
            ? DefaultBaseUrl
            : baseUrl.Trim().TrimEnd('/');
        _model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();
        _maxTokens = maxTokens;
        if (httpClient is null)
        {
            _http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            _ownsHttp = true;
        }
        else
        {
            _http = httpClient;
            _ownsHttp = false;
        }
    }

    public bool HasApiKey => _apiKey.Length > 0;

    public string MessagesUrl => _baseUrl + "/v1/messages";

    public static AnthropicClient FromPreferences(UserPreferences prefs, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(prefs);
        return new AnthropicClient(
            prefs.ResolveAgentApiKey(),
            prefs.ResolveAgentBaseUrl(),
            prefs.ResolveAgentModel(),
            httpClient);
    }

    public async Task<AnthropicTurn> CompleteAsync(
        string system,
        IReadOnlyList<AnthropicToolSchema> tools,
        IReadOnlyList<AgentMessage> messages,
        CancellationToken cancellationToken = default)
    {
        if (!HasApiKey)
            throw new AnthropicClientException(AnthropicClientErrorKind.MissingApiKey, AgentSetup.MissingKeyError);

        using var request = new HttpRequestMessage(HttpMethod.Post, MessagesUrl);
        request.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
        request.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new StringContent(
            BuildRequestBody(system, tools, messages),
            Encoding.UTF8,
            "application/json");

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if ((int)response.StatusCode >= 400)
        {
            var excerpt = body.Length > 500 ? body[..500] : body;
            throw new AnthropicClientException(
                AnthropicClientErrorKind.HttpError,
                $"Anthropic API error ({(int)response.StatusCode}): {excerpt}",
                (int)response.StatusCode);
        }

        return ParseTurn(body);
    }

    internal string BuildRequestBody(
        string system,
        IReadOnlyList<AnthropicToolSchema> tools,
        IReadOnlyList<AgentMessage> messages)
    {
        var body = new JsonObject
        {
            ["model"] = _model,
            ["max_tokens"] = _maxTokens,
            ["stream"] = false,
            ["system"] = system,
            ["messages"] = BuildMessages(messages),
        };

        if (tools.Count > 0)
        {
            var toolBlocks = new JsonArray();
            foreach (var tool in tools)
            {
                JsonNode schema = tool.InputSchema is null
                    ? new JsonObject { ["type"] = "object" }
                    : JsonNode.Parse(tool.InputSchema.ToJsonString())
                      ?? new JsonObject { ["type"] = "object" };
                toolBlocks.Add(new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["input_schema"] = schema,
                });
            }

            body["tools"] = toolBlocks;
        }

        if (string.Equals(_model, DefaultModel, StringComparison.Ordinal))
        {
            body["output_config"] = new JsonObject
            {
                ["effort"] = "low",
            };
        }

        return body.ToJsonString();
    }

    private static JsonArray BuildMessages(IReadOnlyList<AgentMessage> messages)
    {
        var merged = new List<(string Role, JsonArray Content)>();
        foreach (var message in messages)
        {
            if (message.Role == AgentMessageRole.system)
                continue;

            var content = new JsonArray();
            foreach (var block in message.Blocks)
            {
                var node = ToApiContent(block);
                if (node is not null)
                    content.Add(node);
            }

            if (content.Count == 0)
                continue;

            var role = message.Role == AgentMessageRole.assistant ? "assistant" : "user";
            if (merged.Count > 0 && merged[^1].Role == role)
            {
                foreach (var node in content)
                {
                    if (node is not null)
                        merged[^1].Content.Add(node.DeepClone());
                }
            }
            else
            {
                merged.Add((role, content));
            }
        }

        var result = new JsonArray();
        foreach (var (role, content) in merged)
        {
            result.Add(new JsonObject
            {
                ["role"] = role,
                ["content"] = content,
            });
        }

        return result;
    }

    private static JsonObject? ToApiContent(AgentContentBlock block)
    {
        switch (block.Kind)
        {
            case AgentContentBlock.KindText:
                if (string.IsNullOrEmpty(block.Text))
                    return null;
                return new JsonObject { ["type"] = "text", ["text"] = block.Text };

            case AgentContentBlock.KindToolUse:
                JsonNode input;
                try
                {
                    input = JsonNode.Parse(string.IsNullOrWhiteSpace(block.Input) ? "{}" : block.Input)
                            ?? new JsonObject();
                }
                catch (JsonException)
                {
                    input = new JsonObject();
                }

                return new JsonObject
                {
                    ["type"] = "tool_use",
                    ["id"] = block.Id ?? "",
                    ["name"] = block.Name ?? "",
                    ["input"] = input,
                };

            case AgentContentBlock.KindToolResult:
                var content = new JsonArray();
                foreach (var item in block.Content ?? [])
                {
                    if (item.Kind == AgentToolResultBlock.KindImage
                        && !string.IsNullOrEmpty(item.Base64)
                        && !string.IsNullOrEmpty(item.MediaType))
                    {
                        content.Add(new JsonObject
                        {
                            ["type"] = "image",
                            ["source"] = new JsonObject
                            {
                                ["type"] = "base64",
                                ["media_type"] = item.MediaType,
                                ["data"] = item.Base64,
                            },
                        });
                    }
                    else
                    {
                        content.Add(new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = item.Text ?? "",
                        });
                    }
                }

                return new JsonObject
                {
                    ["type"] = "tool_result",
                    ["tool_use_id"] = block.ToolUseId ?? "",
                    ["content"] = content,
                    ["is_error"] = block.IsError ?? false,
                };

            default:
                return null;
        }
    }

    internal static AnthropicTurn ParseTurn(string body)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new AnthropicClientException(
                AnthropicClientErrorKind.InvalidResponse,
                "Anthropic API returned invalid JSON: " + ex.Message);
        }

        if (root is not JsonObject obj)
        {
            throw new AnthropicClientException(
                AnthropicClientErrorKind.InvalidResponse,
                "Anthropic API returned a non-object response.");
        }

        var stop = obj["stop_reason"]?.GetValue<string>() ?? "end_turn";
        var blocks = new List<AgentContentBlock>();
        if (obj["content"] is JsonArray content)
        {
            foreach (var item in content)
            {
                if (item is not JsonObject block)
                    continue;
                var type = block["type"]?.GetValue<string>();
                if (type == "text")
                {
                    var text = block["text"]?.GetValue<string>() ?? "";
                    if (text.Length > 0)
                        blocks.Add(AgentContentBlock.FromText(text));
                }
                else if (type == "tool_use")
                {
                    var id = block["id"]?.GetValue<string>() ?? "";
                    var name = block["name"]?.GetValue<string>() ?? "";
                    var input = block["input"] is null ? "{}" : block["input"]!.ToJsonString();
                    blocks.Add(AgentContentBlock.ToolUse(id, name, input));
                }
            }
        }

        return new AnthropicTurn { StopReason = stop, Blocks = blocks };
    }

    public void DisposeOwned()
    {
        if (_ownsHttp)
            _http.Dispose();
    }
}
