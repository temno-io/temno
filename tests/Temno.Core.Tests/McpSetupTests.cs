using Temno.Core.Account;
using Temno.Environments;
using Temno.Mcp;
using Xunit;

namespace Temno.Core.Tests;

public class McpSetupTests
{
    [Fact]
    public void LoadOrCreate_MintsStableTokenAndDefaultsEnabled()
    {
        var path = Path.Combine(Path.GetTempPath(), "temno-mcp-tests", Guid.NewGuid().ToString("N"), "mcp-preferences.json");
        try
        {
            var first = McpPreferences.LoadOrCreate(path);
            Assert.True(first.Enabled);
            Assert.True(McpPreferences.LooksLikeToken(first.AuthToken));

            var second = McpPreferences.LoadOrCreate(path);
            Assert.Equal(first.AuthToken, second.AuthToken);
            Assert.True(second.Enabled);
        }
        finally
        {
            TryDeleteTree(Path.GetDirectoryName(path));
        }
    }

    [Fact]
    public void Load_MissingEnabledDefaultsOn()
    {
        var path = Path.Combine(Path.GetTempPath(), "temno-mcp-tests", Guid.NewGuid().ToString("N"), "mcp-preferences.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, """{"authToken":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""");
            var prefs = McpPreferences.Load(path);
            Assert.True(prefs.Enabled);
            Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", prefs.AuthToken);
        }
        finally
        {
            TryDeleteTree(Path.GetDirectoryName(path));
        }
    }

    [Fact]
    public void Save_RoundTripsDisabled()
    {
        var path = Path.Combine(Path.GetTempPath(), "temno-mcp-tests", Guid.NewGuid().ToString("N"), "mcp-preferences.json");
        try
        {
            var prefs = new McpPreferences
            {
                Enabled = false,
                AuthToken = McpPreferences.NewAuthToken(),
            };
            McpPreferences.Save(prefs, path);
            var loaded = McpPreferences.Load(path);
            Assert.False(loaded.Enabled);
            Assert.Equal(prefs.AuthToken, loaded.AuthToken);
        }
        finally
        {
            TryDeleteTree(Path.GetDirectoryName(path));
        }
    }

    [Fact]
    public void ClientSetup_IncludesLocalAgentsAndAuthHeader()
    {
        const string endpoint = "http://127.0.0.1:19789/mcp";
        const string token = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var snippets = McpClientSetup.Build(endpoint, token);
        var ids = snippets.Select(s => s.Id).ToArray();
        Assert.Contains("cursor", ids);
        Assert.Contains("claude-code", ids);
        Assert.Contains("claude-desktop", ids);
        Assert.Contains("codex", ids);
        Assert.Contains("grok", ids);
        Assert.Contains("gemini", ids);

        var cursor = snippets.Single(s => s.Id == "cursor").Body;
        Assert.Contains("\"type\": \"http\"", cursor);
        Assert.Contains(endpoint, cursor);
        Assert.Contains(McpDefaults.AuthHeaderName, cursor);
        Assert.Contains(token, cursor);

        var claude = snippets.Single(s => s.Id == "claude-code").Body;
        Assert.Contains("--transport http", claude);
        Assert.Contains(McpDefaults.AuthHeaderName, claude);

        var grok = snippets.Single(s => s.Id == "grok").Body;
        Assert.Contains("grok mcp add", grok);
        Assert.Contains("--header", grok);

        var gemini = snippets.Single(s => s.Id == "gemini").Body;
        Assert.Contains("httpUrl", gemini);

        var codex = snippets.Single(s => s.Id == "codex");
        Assert.Contains("cannot reach 127.0.0.1", codex.Intro);
        Assert.Contains("http_headers", codex.Body);
    }

    [Fact]
    public void Defaults_EndpointMatchesLoopbackContract()
    {
        // WP-334: the Mac contract's port is the production app's; a development build (Temno-Dev, and every test
        // build) binds the next port, so both apps answer at once.
        Assert.Equal(19789, McpDefaults.PortFor(TemnoEnvironment.Production));
        Assert.Equal(19790, McpDefaults.PortFor(TemnoEnvironment.Development));
        Assert.Equal(TemnoEnvironment.Development, HostedEndpoints.BuildEnvironment);
        Assert.Equal("http://127.0.0.1:19790/mcp", McpDefaults.Endpoint);
        Assert.Equal(19790, new McpServer().Port);
    }

    private static void TryDeleteTree(string? dir)
    {
        try
        {
            if (dir is not null && Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // temp cleanup is best-effort
        }
    }
}
