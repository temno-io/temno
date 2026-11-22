using System.Runtime.CompilerServices;
using Temno.Core.Account;

namespace Temno.Core.Tests;

/// <summary>
/// Points <see cref="DevelopmentBackendFile"/> at a path that never exists before any test runs,
/// so no test resolves hosted endpoints from the developer's real
/// <c>%LOCALAPPDATA%\Temno-Dev\development-backend.json</c> (WP-260, WP-334). Per-test files layer on top.
/// </summary>
internal static class DevelopmentBackendFileProcessDefault
{
    public static string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(),
        "temno-tests-no-developer-backend-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N") + ".json");

    [ModuleInitializer]
    internal static void Install() => DevelopmentBackendFile.SetDefaultPathForTests(Path);
}
