using Temno.Core.Project;
using Xunit;

namespace Temno.Core.Tests;

[Collection("EnvRegistry")]
public sealed class ProjectRegistryTests
{
    [Fact]
    public void Register_Load_Remove_RoundTrip()
    {
        var dir = Path.Combine(Path.GetTempPath(), "temno-registry-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var package = Path.Combine(dir, "Demo.palmier");
        Directory.CreateDirectory(package);
        var registryFile = Path.Combine(dir, "project-registry.json");

        var prev = Environment.GetEnvironmentVariable("TEMNO_REGISTRY_PATH");
        try
        {
            Environment.SetEnvironmentVariable("TEMNO_REGISTRY_PATH", registryFile);

            var entry = ProjectRegistry.Register(package);
            Assert.Equal(package, new Uri(entry.Url).LocalPath, ignoreCase: true);
            Assert.Equal("Demo", entry.Name);
            Assert.True(entry.IsAccessible);

            var loaded = ProjectRegistry.Load();
            Assert.Single(loaded);
            Assert.Equal(entry.Id, loaded[0].Id);

            // Re-register bumps last opened, keeps id
            var again = ProjectRegistry.Register(package);
            Assert.Equal(entry.Id, again.Id);
            Assert.True(again.LastOpenedDate >= entry.LastOpenedDate);

            ProjectRegistry.Remove(package);
            Assert.Empty(ProjectRegistry.Load());
        }
        finally
        {
            Environment.SetEnvironmentVariable("TEMNO_REGISTRY_PATH", prev);
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void Load_CorruptRegistry_ReportsInvalidData()
    {
        var dir = Path.Combine(Path.GetTempPath(), "temno-registry-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var registryFile = Path.Combine(dir, "project-registry.json");
        File.WriteAllText(registryFile, "{not-json");
        var previous = Environment.GetEnvironmentVariable("TEMNO_REGISTRY_PATH");
        try
        {
            Environment.SetEnvironmentVariable("TEMNO_REGISTRY_PATH", registryFile);
            Assert.Throws<InvalidDataException>(() => ProjectRegistry.Load());
        }
        finally
        {
            Environment.SetEnvironmentVariable("TEMNO_REGISTRY_PATH", previous);
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void Save_UnwritableRegistryPath_ReportsFailure()
    {
        var dir = Path.Combine(Path.GetTempPath(), "temno-registry-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var blockedParent = Path.Combine(dir, "blocked");
        File.WriteAllText(blockedParent, "not a directory");
        var previous = Environment.GetEnvironmentVariable("TEMNO_REGISTRY_PATH");
        try
        {
            Environment.SetEnvironmentVariable("TEMNO_REGISTRY_PATH", Path.Combine(blockedParent, "project-registry.json"));
            Assert.ThrowsAny<IOException>(() => ProjectRegistry.Save([]));
        }
        finally
        {
            Environment.SetEnvironmentVariable("TEMNO_REGISTRY_PATH", previous);
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }
}
