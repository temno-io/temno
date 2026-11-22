using System.Text.Json.Nodes;

namespace Temno.Mcp;

/// <summary>Copy-paste configs so local MCP clients can reach the loopback Temno server.</summary>
public readonly record struct McpClientSnippet(string Id, string Name, string Intro, string Body);

public static class McpClientSetup
{
    public static IReadOnlyList<McpClientSnippet> Build(string endpoint, string authToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(authToken);

        var headerName = McpDefaults.AuthHeaderName;
        var headerLine = $"{headerName}: {authToken}";

        return
        [
            new(
                "cursor",
                "Cursor",
                "Add this to ~/.cursor/mcp.json (or Cursor Settings → MCP). Grok in Cursor uses the same server.",
                ToPrettyJson(new JsonObject
                {
                    ["mcpServers"] = new JsonObject
                    {
                        ["temno"] = new JsonObject
                        {
                            ["type"] = "http",
                            ["url"] = endpoint,
                            ["headers"] = new JsonObject { [headerName] = authToken },
                        },
                    },
                })),
            new(
                "claude-code",
                "Claude Code",
                "Run once in a terminal. Restart Claude Code afterward.",
                $"claude mcp add --transport http temno {endpoint} --header \"{headerLine}\""),
            new(
                "claude-desktop",
                "Claude Desktop",
                "Settings → Developer → Edit Config, then merge this into mcpServers. Requires Node.js (npx).",
                ToPrettyJson(new JsonObject
                {
                    ["mcpServers"] = new JsonObject
                    {
                        ["temno"] = new JsonObject
                        {
                            ["command"] = "npx",
                            ["args"] = new JsonArray(
                                "-y",
                                "mcp-remote",
                                endpoint,
                                "--allow-http",
                                "--transport",
                                "http-only",
                                "--header",
                                headerLine),
                        },
                    },
                })),
            new(
                "codex",
                "Codex / ChatGPT (local)",
                "Codex on this PC can edit the open project. ChatGPT in a browser cannot reach 127.0.0.1 — do not tunnel this port. After `codex mcp add`, put the header in ~/.codex/config.toml.",
                $"codex mcp add temno --url {endpoint}\n\n" +
                $"[mcp_servers.temno]\n" +
                $"url = \"{endpoint}\"\n" +
                $"http_headers = {{ \"{headerName}\" = \"{authToken}\" }}"),
            new(
                "grok",
                "Grok CLI",
                "Grok CLI on this PC. grok.com cannot see loopback.",
                $"grok mcp add --transport http temno {endpoint} --header \"{headerLine}\""),
            new(
                "gemini",
                "Gemini CLI / Antigravity",
                "Merge into ~/.gemini/settings.json (Gemini CLI) or the Antigravity MCP settings.",
                ToPrettyJson(new JsonObject
                {
                    ["mcpServers"] = new JsonObject
                    {
                        ["temno"] = new JsonObject
                        {
                            ["httpUrl"] = endpoint,
                            ["headers"] = new JsonObject { [headerName] = authToken },
                        },
                    },
                })),
        ];
    }

    public static IReadOnlyList<McpClientSnippet> ForCurrent(string? authToken)
        => string.IsNullOrWhiteSpace(authToken)
            ? []
            : Build(McpDefaults.Endpoint, authToken);

    private static string ToPrettyJson(JsonObject node)
        => node.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
}
