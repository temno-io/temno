using System.Linq;
using Temno.Mcp;
using Xunit;

namespace Temno.Core.Tests;

public class McpRegistryTests
{
    [Fact]
    public void TestToolRegistry_ToolCountAndSchema()
    {
        var tools = McpToolRegistry.Tools;

        // 73 prior tools plus image operations (WP-232) and quote_media (WP-242).
        Assert.Equal(77, tools.Count);

        // Verify key tools exist
        var toolNames = tools.Select(t => t.Name).ToHashSet();
        Assert.Contains("get_timeline", toolNames);
        Assert.Contains("add_clips", toolNames);
        Assert.Contains("inspect_media", toolNames);
        Assert.Contains("open_project", toolNames);
        Assert.Contains("generate_google", toolNames);
        Assert.Contains("quote_image", toolNames);
        Assert.Contains("quote_media", toolNames);
        Assert.Contains("edit_image", toolNames);
        Assert.Contains("manage_multicam", toolNames);
        Assert.Contains("change_cam", toolNames);
        Assert.Contains("get_multicam", toolNames);
        Assert.Contains("undo", toolNames);
        Assert.Contains("redo", toolNames);
        Assert.Contains("manage_project", toolNames);
        var manageProject = tools.First(t => t.Name == "manage_project");
        Assert.Contains("action", manageProject.InputSchema["required"]!.AsArray().Select(n => n!.GetValue<string>()));

        // Check a specific tool's schema structure
        var getTimeline = tools.First(t => t.Name == "get_timeline");
        Assert.NotNull(getTimeline.InputSchema);
        Assert.Equal("object", getTimeline.InputSchema["type"]?.GetValue<string>());
        var generateGoogle = tools.First(t => t.Name == "generate_google");
        var description = generateGoogle.Description ?? "";
        Assert.DoesNotContain("Vertex", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("alias", description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async System.Threading.Tasks.Task TestMcpServer_LifecycleAndRequests()
    {
        using var client = new System.Net.Http.HttpClient();
        // A hardcoded port makes this test fail whenever anything else on the machine happens to
        // hold it (an editor's language server, another test run), which reads as a product failure.
        var port = FreeLoopbackPort();
        var origin = $"http://127.0.0.1:{port}";
        var server = new McpServer("127.0.0.1", port);
        server.Start();
        client.DefaultRequestHeaders.Add(McpDefaults.AuthHeaderName, server.AuthToken);

        try
        {
            // Test 1: GET /health (no token)
            using var unauthClient = new System.Net.Http.HttpClient();
            var healthResponse = await unauthClient.GetAsync(origin + "/health");
            Assert.Equal(System.Net.HttpStatusCode.OK, healthResponse.StatusCode);
            var healthContent = await healthResponse.Content.ReadAsStringAsync();
            Assert.Contains("healthy", healthContent);

            var mcpGet = await unauthClient.GetAsync(origin + "/mcp");
            Assert.Equal(System.Net.HttpStatusCode.MethodNotAllowed, mcpGet.StatusCode);

            var wellKnown = await unauthClient.GetAsync(origin + "/.well-known/oauth-protected-resource");
            Assert.Equal(System.Net.HttpStatusCode.OK, wellKnown.StatusCode);

            // Test 2: POST /mcp with initialize method
            var initPayload = new
            {
                jsonrpc = "2.0",
                method = "initialize",
                id = 1,
                @params = new
                {
                    protocolVersion = "2024-11-05",
                    capabilities = new { },
                    clientInfo = new { name = "test-client", version = "1.0" }
                }
            };
            var initContent = new System.Net.Http.StringContent(
                System.Text.Json.JsonSerializer.Serialize(initPayload),
                System.Text.Encoding.UTF8,
                "application/json"
            );
            var initResponse = await client.PostAsync(origin + "/mcp", initContent);
            Assert.Equal(System.Net.HttpStatusCode.OK, initResponse.StatusCode);
            Assert.False(initResponse.Headers.Contains("Access-Control-Allow-Origin"));
            var initResponseBody = await initResponse.Content.ReadAsStringAsync();
            Assert.Contains("temno", initResponseBody);

            // Test 3: POST /mcp with tools/list method
            var listPayload = new
            {
                jsonrpc = "2.0",
                method = "tools/list",
                id = 2
            };
            var listContent = new System.Net.Http.StringContent(
                System.Text.Json.JsonSerializer.Serialize(listPayload),
                System.Text.Encoding.UTF8,
                "application/json"
            );
            var listResponse = await client.PostAsync(origin + "/mcp", listContent);
            Assert.Equal(System.Net.HttpStatusCode.OK, listResponse.StatusCode);
            var listResponseBody = await listResponse.Content.ReadAsStringAsync();
            Assert.Contains("get_timeline", listResponseBody);
            Assert.Contains("manage_project", listResponseBody);

            using var missingToken = new System.Net.Http.HttpRequestMessage(
                System.Net.Http.HttpMethod.Post,
                origin + "/mcp");
            missingToken.Content = new System.Net.Http.StringContent(
                System.Text.Json.JsonSerializer.Serialize(listPayload),
                System.Text.Encoding.UTF8,
                "application/json");
            var unauthorized = await unauthClient.SendAsync(missingToken);
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, unauthorized.StatusCode);
            Assert.False(unauthorized.Headers.Contains("Access-Control-Allow-Origin"));

            // Browser requests are allowed only from loopback pages; arbitrary websites cannot
            // drive mutation tools through permissive CORS.
            using var foreignRequest = new System.Net.Http.HttpRequestMessage(
                System.Net.Http.HttpMethod.Get,
                origin + "/health");
            foreignRequest.Headers.Add("Origin", "https://example.com");
            var foreignResponse = await unauthClient.SendAsync(foreignRequest);
            Assert.Equal(System.Net.HttpStatusCode.Forbidden, foreignResponse.StatusCode);
            Assert.False(foreignResponse.Headers.Contains("Access-Control-Allow-Origin"));

            using var localPreflight = new System.Net.Http.HttpRequestMessage(
                System.Net.Http.HttpMethod.Options,
                origin + "/mcp");
            localPreflight.Headers.Add("Origin", "http://localhost:3000");
            var localResponse = await unauthClient.SendAsync(localPreflight);
            Assert.Equal(System.Net.HttpStatusCode.NoContent, localResponse.StatusCode);
            Assert.False(localResponse.Headers.Contains("Access-Control-Allow-Origin"));
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public void McpServer_RejectsNonLoopbackBinding()
    {
        var error = Assert.Throws<ArgumentException>(() => new McpServer("0.0.0.0", 19789));
        Assert.Contains("loopback", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Asks the OS for an unused loopback port instead of assuming one is free.</summary>
    private static int FreeLoopbackPort()
    {
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        try { return ((System.Net.IPEndPoint)probe.LocalEndpoint).Port; }
        finally { probe.Stop(); }
    }

}
