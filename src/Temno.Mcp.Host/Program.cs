using System;
using Temno.Mcp;

namespace Temno.Mcp.Host;

class Program
{
    static void Main(string[] args)
    {
        Console.WriteLine("Starting Temno Windows MCP Server Skeleton...");
        Console.WriteLine($"Binding to http://{McpDefaults.Host}:{McpDefaults.Port}/");

        // WP-309: the same skills the app offers.
        Temno.Core.Agent.Skills.SkillLibrary.Current = Temno.Core.Agent.Skills.SkillStore.ForUser();
        var prefs = McpPreferences.LoadOrCreate();
        var server = new McpServer(authToken: prefs.AuthToken);
        server.Start();
        Console.WriteLine("MCP Server started successfully.");
        Console.WriteLine($"POST {McpDefaults.AuthHeaderName}: {server.AuthToken}");
        Console.WriteLine("Press Ctrl+C or type 'quit' to exit.");

        var keepRunning = true;
        Console.CancelKeyPress += (sender, e) =>
        {
            Console.WriteLine("Shutting down cleanly...");
            server.Stop();
            keepRunning = false;
            e.Cancel = true;
        };

        while (keepRunning)
        {
            var line = Console.ReadLine();
            if (line != null && line.Trim().Equals("quit", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
        }

        if (keepRunning)
        {
            Console.WriteLine("Shutting down cleanly...");
            server.Stop();
        }

        Console.WriteLine("MCP Server stopped.");
    }
}
