using System.Runtime.CompilerServices;
using Temno.Core.Agent;

namespace Temno.Core.Tests;

/// <summary>
/// Points <see cref="HostedTurnOrigins"/> at a file of this test run before any test runs, so no test reads or
/// writes the developer's real <c>%LOCALAPPDATA%\Temno-Dev\hosted-turn-origins.json</c> (WP-400). Records are keyed
/// by a fresh retry key per turn, so tests sharing this file never match each other's turns.
/// </summary>
internal static class HostedTurnOriginsProcessDefault
{
    public static string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(),
        "temno-tests-hosted-turn-origins-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N") + ".json");

    [ModuleInitializer]
    internal static void Install() => HostedTurnOrigins.SetDefaultPathForTests(Path);
}
