using Temno.Core.Account;
using Temno.Environments;

namespace Temno.Mcp;

/// <summary>
/// Mac: MCP binds 127.0.0.1:19789 only. WP-334: that is the production app's port; a development build (Temno-Dev)
/// binds 19790, so both apps answer at once and an agent testing Temno-Dev never reaches the production app.
/// </summary>
public static class McpDefaults
{
    public const string Host = "127.0.0.1";
    public const int ProductionPort = 19789;
    public const int DevelopmentPort = 19790;

    /// <summary>This build's port, from the environment compiled into Temno.Core.</summary>
    public static int Port => PortFor(HostedEndpoints.BuildEnvironment);

    public static int PortFor(TemnoEnvironment environment)
        => environment == TemnoEnvironment.Production ? ProductionPort : DevelopmentPort;
    public const string Path = "/mcp";
    /// <summary>HTTP header required on POST /mcp. GET /health stays unauthenticated.</summary>
    public const string AuthHeaderName = "X-Temno-Mcp-Token";

    public static string Endpoint => $"http://{Host}:{Port}{Path}";
}
