using System.Runtime.CompilerServices;
using Temno.Core.IO;

namespace Temno.Core.Tests;

/// <summary>
/// Points <see cref="ExternalMediaGrants"/> at a file of this test run before any test runs, so no test
/// reads or writes the developer's real <c>%LOCALAPPDATA%\Temno-Dev\external-media-grants.json</c> (WP-399).
/// Grants are keyed by package path and every test's packages live in its own temp folder, so tests
/// sharing this file never see each other's grants.
/// </summary>
internal static class ExternalMediaGrantsProcessDefault
{
    public static string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(),
        "temno-tests-external-media-grants-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N") + ".json");

    [ModuleInitializer]
    internal static void Install() => ExternalMediaGrants.SetDefaultPathForTests(Path);
}
