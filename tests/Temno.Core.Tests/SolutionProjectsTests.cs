using System.Text.RegularExpressions;
using Xunit;

namespace Temno.Core.Tests;

// TemnoWindows.sln is the desktop app's solution, and the public repository carries it as it is. A project it lists
// must therefore be one the public repository carries too: with the hosted worker in the solution, the public tree
// failed `dotnet build TemnoWindows.sln` at its first step. Hosted services are built by project path instead.
public sealed class SolutionProjectsTests
{
    [Fact]
    public void Every_project_the_solution_lists_is_one_the_public_repository_carries()
    {
        var root = BuildProvenanceTests.RepoRoot();
        var solution = File.ReadAllText(Path.Combine(root, "TemnoWindows.sln"));
        var projects = Regex.Matches(solution, "\"([^\"]+\\.csproj)\"")
            .Select(match => match.Groups[1].Value.Replace('\\', '/'))
            .ToList();
        Assert.NotEmpty(projects);

        // True in either repository: a clone can only build projects it has.
        foreach (var project in projects)
            Assert.True(File.Exists(Path.Combine(root, project)), $"TemnoWindows.sln lists {project}, which this tree does not have.");

        // Only the development repository has the export's allowlist; there, each project must be on it. The tests
        // ship in the export too, where the check above is the whole test.
        var allowlist = Path.Combine(root, "scripts", "public-export", "allowlist.txt");
        if (!File.Exists(allowlist))
            return;
        var lines = File.ReadAllLines(allowlist)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToList();
        foreach (var project in projects)
        {
            var exported = lines.Any(line => line.EndsWith("/**", StringComparison.Ordinal)
                ? project.StartsWith(line[..^2], StringComparison.Ordinal)
                : string.Equals(line, project, StringComparison.Ordinal));
            Assert.True(exported, $"TemnoWindows.sln lists {project}, which scripts/public-export/allowlist.txt does not export. " +
                                  "Keep hosted projects out of this solution, or the public repository stops building.");
        }
    }
}
