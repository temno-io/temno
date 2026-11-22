using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Temno.Core.IO;
using Temno.Core.Models;
using Temno.Core.Project;
using Temno.Core.Session;
using Temno.Mcp;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// WP-193 step 1: the dual-read seams. Each test pins one side of the rename contract —
/// read both, write the new spelling only — so step 2's mechanical rename cannot silently
/// drop a compatibility path.
/// </summary>
[Collection("EnvRegistry")]
public sealed class TemnoDualReadTests
{
    private static string TempRoot(string prefix) =>
        Path.Combine(Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void PackageExtension_AcceptsBothWritesTemno()
    {
        Assert.True(ProjectConstants.IsPackageExtension(".temno"));
        Assert.True(ProjectConstants.IsPackageExtension(".palmier"));
        Assert.True(ProjectConstants.IsPackageExtension("temno"));
        Assert.True(ProjectConstants.IsPackageExtension("palmier"));
        Assert.False(ProjectConstants.IsPackageExtension(".mp4"));

        Assert.True(ProjectConstants.HasPackageExtension(@"C:\proj\Show.temno"));
        Assert.True(ProjectConstants.HasPackageExtension(@"C:\proj\Show.palmier"));
        Assert.False(ProjectConstants.HasPackageExtension(@"C:\proj\Show.mp4"));

        Assert.Equal("Show.temno", ProjectConstants.EnsureWriteExtension("Show"));
        Assert.Equal("Show.temno", ProjectConstants.EnsureWriteExtension("Show.temno"));
        // Strict per owner decision 2026-09-05: every write emits .temno, no exceptions.
        // An explicit legacy suffix is rewritten; .palmier is read-only interchange.
        Assert.Equal("Show.temno", ProjectConstants.EnsureWriteExtension("Show.palmier"));
    }

    [Fact]
    public void Session_RoundTripsTemnoPackage()
    {
        var package = TempRoot("temno-pkg") + ".temno";
        try
        {
            var session = ProjectSession.CreateNew("TemnoWrite");
            session.MarkDirty();
            session.Save(package);

            var reopened = ProjectSession.Open(package);
            Assert.Equal("TemnoWrite", reopened.ActiveTimeline.Name);
        }
        finally
        {
            if (Directory.Exists(package)) Directory.Delete(package, recursive: true);
        }
    }

    [Fact]
    public void Session_OpensLegacyPalmierPackage()
    {
        var package = TempRoot("temno-legacy") + ".palmier";
        try
        {
            var session = ProjectSession.CreateNew("LegacyRead");
            session.MarkDirty();
            session.Save(package);

            var reopened = ProjectSession.Open(package);
            Assert.Equal("LegacyRead", reopened.ActiveTimeline.Name);
        }
        finally
        {
            if (Directory.Exists(package)) Directory.Delete(package, recursive: true);
        }
    }

    [Fact]
    public void EnvCompat_TemnoWinsOverLegacy()
    {
        const string temno = "TEMNO_WP193_SEAMTEST";
        var legacy = EnvCompat.ToLegacyName(temno)!;
        var prevNew = Environment.GetEnvironmentVariable(temno);
        var prevLegacy = Environment.GetEnvironmentVariable(legacy);
        var events = 0;
        void Count(string _) => events++;
        EnvCompat.LegacyFallbackObserved += Count;
        try
        {
            Environment.SetEnvironmentVariable(temno, "new");
            Environment.SetEnvironmentVariable(legacy, "old");
            Assert.Equal("new", EnvCompat.Get(temno));
            Assert.Equal(0, events);
        }
        finally
        {
            EnvCompat.LegacyFallbackObserved -= Count;
            Environment.SetEnvironmentVariable(temno, prevNew);
            Environment.SetEnvironmentVariable(legacy, prevLegacy);
        }
    }

    [Fact]
    public void EnvCompat_LegacyHonoredWithOneDeprecationLine()
    {
        const string temno = "TEMNO_WP193_SEAMTEST_SOLO";
        var legacy = EnvCompat.ToLegacyName(temno)!;
        var prevNew = Environment.GetEnvironmentVariable(temno);
        var prevLegacy = Environment.GetEnvironmentVariable(legacy);
        var lines = new List<string>();
        void Capture(string line) => lines.Add(line);
        EnvCompat.LegacyFallbackObserved += Capture;
        try
        {
            Environment.SetEnvironmentVariable(temno, null);
            Environment.SetEnvironmentVariable(legacy, "old");
            Assert.Equal("old", EnvCompat.Get(temno));
            Assert.Equal("old", EnvCompat.Get(temno));
            Assert.Single(lines);
            Assert.Contains(legacy, lines[0]);
            Assert.Contains(temno, lines[0]);
        }
        finally
        {
            EnvCompat.LegacyFallbackObserved -= Capture;
            Environment.SetEnvironmentVariable(temno, prevNew);
            Environment.SetEnvironmentVariable(legacy, prevLegacy);
        }
    }

    [Fact]
    public void ProjectRegistry_LegacyEnvPathStillWorks()
    {
        var dir = TempRoot("temno-regcompat");
        Directory.CreateDirectory(dir);
        var registryFile = Path.Combine(dir, "project-registry.json");
        var package = Path.Combine(dir, "Demo.palmier");
        Directory.CreateDirectory(package);
        var prevNew = Environment.GetEnvironmentVariable("TEMNO_REGISTRY_PATH");
        var prevLegacy = Environment.GetEnvironmentVariable("PALMIER_REGISTRY_PATH");
        try
        {
            Environment.SetEnvironmentVariable("TEMNO_REGISTRY_PATH", null);
            Environment.SetEnvironmentVariable("PALMIER_REGISTRY_PATH", registryFile);
            var entry = ProjectRegistry.Register(package);
            Assert.Equal("Demo", entry.Name);
            Assert.Single(ProjectRegistry.Load());
        }
        finally
        {
            Environment.SetEnvironmentVariable("TEMNO_REGISTRY_PATH", prevNew);
            Environment.SetEnvironmentVariable("PALMIER_REGISTRY_PATH", prevLegacy);
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void UserDataMigration_CopiesOnlyRegistryPrefsAndCaches()
    {
        var root = TempRoot("temno-migrate");
        var legacyDocs = Path.Combine(root, "old-docs");
        var freshDocs = Path.Combine(root, "new-docs");
        var legacyLad = Path.Combine(root, "old-lad");
        var freshLad = Path.Combine(root, "new-lad");
        try
        {
            Directory.CreateDirectory(Path.Combine(legacyDocs, "Projects", "55.palmier"));
            File.WriteAllText(Path.Combine(legacyDocs, "project-registry.json"), """{"v":1}""");
            File.WriteAllText(Path.Combine(legacyDocs, "Projects", "55.palmier", "project.json"), "{}");
            Directory.CreateDirectory(Path.Combine(legacyLad, "cache", "thumbnails"));
            File.WriteAllText(Path.Combine(legacyLad, "user-preferences.json"), """{"v":1}""");
            File.WriteAllText(Path.Combine(legacyLad, "cache", "thumbnails", "t.jpg"), "x");

            Assert.True(UserDataMigration.MigrateAll(legacyDocs, freshDocs, legacyLad, freshLad));
            Assert.Equal("""{"v":1}""", File.ReadAllText(Path.Combine(freshDocs, "project-registry.json")));
            Assert.Equal("""{"v":1}""", File.ReadAllText(Path.Combine(freshLad, "user-preferences.json")));
            Assert.True(File.Exists(Path.Combine(freshLad, "cache", "thumbnails", "t.jpg")));
            // Copy, never move: the sources are intact.
            Assert.True(File.Exists(Path.Combine(legacyDocs, "project-registry.json")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void UserDataMigration_NeverCopiesProjects()
    {
        var root = TempRoot("temno-migrate-noproj");
        var legacyDocs = Path.Combine(root, "old-docs");
        var freshDocs = Path.Combine(root, "new-docs");
        var legacyLad = Path.Combine(root, "old-lad");
        var freshLad = Path.Combine(root, "new-lad");
        try
        {
            Directory.CreateDirectory(Path.Combine(legacyDocs, "Projects", "55.palmier"));
            File.WriteAllText(Path.Combine(legacyDocs, "project-registry.json"), """{"v":1}""");
            File.WriteAllText(Path.Combine(legacyDocs, "Projects", "55.palmier", "project.json"), "{}");

            UserDataMigration.MigrateAll(legacyDocs, freshDocs, legacyLad, freshLad);
            Assert.False(Directory.Exists(Path.Combine(freshDocs, "Projects")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void UserDataMigration_RegistryCopyKeepsLegacyUrls()
    {
        var root = TempRoot("temno-migrate-urls");
        var legacyDocs = Path.Combine(root, "old-docs");
        var freshDocs = Path.Combine(root, "new-docs");
        var legacyLad = Path.Combine(root, "old-lad");
        var freshLad = Path.Combine(root, "new-lad");
        try
        {
            const string registry = """{"entries":[{"url":"file:///C:/Users/x/Documents/Palmier%20Pro/Projects/55.palmier"}]}""";
            Directory.CreateDirectory(legacyDocs);
            File.WriteAllText(Path.Combine(legacyDocs, "project-registry.json"), registry);

            UserDataMigration.MigrateAll(legacyDocs, freshDocs, legacyLad, freshLad);
            // Byte-identical: the entries still point at the real (legacy-tree) packages.
            Assert.Equal(registry, File.ReadAllText(Path.Combine(freshDocs, "project-registry.json")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void UserDataMigration_UnreadableFileDoesNotThrow()
    {
        // Exclusive lock stands in for an unreadable file (the ACL-denied case that used
        // to crash OnLaunched): the copy throws, the catch-all skips it, the rest migrates.
        var root = TempRoot("temno-migrate-locked");
        var legacyDocs = Path.Combine(root, "old-docs");
        var freshDocs = Path.Combine(root, "new-docs");
        var legacyLad = Path.Combine(root, "old-lad");
        var freshLad = Path.Combine(root, "new-lad");
        var locked = Path.Combine(legacyLad, "cache", "locked.bin");
        try
        {
            Directory.CreateDirectory(Path.Combine(legacyLad, "cache"));
            Directory.CreateDirectory(legacyDocs);
            File.WriteAllText(Path.Combine(legacyDocs, "project-registry.json"), """{"v":1}""");
            File.WriteAllText(locked, "x");
            using var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);

            var ex = Record.Exception(() =>
                UserDataMigration.MigrateAll(legacyDocs, freshDocs, legacyLad, freshLad));
            Assert.Null(ex);
            // The rest of the migration still happened around the unreadable file.
            Assert.True(File.Exists(Path.Combine(freshDocs, "project-registry.json")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void PresetStore_ReadsLegacyWritesNew()
    {
        var root = TempRoot("temno-presets");
        var newBase = Path.Combine(root, "new");
        var legacyBase = Path.Combine(root, "legacy");
        try
        {
            var legacyDir = Path.Combine(legacyBase, "presets");
            Directory.CreateDirectory(legacyDir);
            var seeded = new List<TextStylePreset>
            {
                new("Legacy Look", new TextStyle { FontName = "Arial", FontSize = 48 }),
            };
            var legacyFile = Path.Combine(legacyDir, "text-styles.json");
            File.WriteAllText(legacyFile, JsonSerializer.Serialize(seeded));
            var legacyBytes = File.ReadAllBytes(legacyFile);

            var store = new PresetStore(newBase, legacyBase);
            Assert.Contains(store.TextStyles, p => p.Name == "Legacy Look");

            store.SaveTextStyle("New Look", new TextStyle { FontName = "Arial", FontSize = 24 });
            Assert.True(File.Exists(Path.Combine(newBase, "presets", "text-styles.json")));
            Assert.Equal(legacyBytes, File.ReadAllBytes(legacyFile));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void ProjectTemplateStore_NewWinsLegacyListed()
    {
        var root = TempRoot("temno-templates");
        var newBase = Path.Combine(root, "new");
        var legacyBase = Path.Combine(root, "legacy");
        try
        {
            var legacyStore = new ProjectTemplateStore(legacyBase);
            legacyStore.Save("Shared", ProjectSession.CreateNew("Legacy").ProjectFile);
            legacyStore.Save("LegacyOnly", ProjectSession.CreateNew("Legacy").ProjectFile);

            var store = new ProjectTemplateStore(newBase, legacyBase);
            store.Save("Shared", ProjectSession.CreateNew("New").ProjectFile);

            var listed = store.List().ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("LegacyOnly", listed);
            // New directory wins on name collisions.
            Assert.Equal(
                Path.Combine(newBase, "templates", "Shared.json"),
                listed["Shared"].Path);
            Assert.Equal(
                Path.Combine(legacyBase, "templates", "LegacyOnly.json"),
                listed["LegacyOnly"].Path);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task McpResources_ListBothSchemesAndReadEchoesBoth()
    {
        using var client = new HttpClient();
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var server = new McpServer("127.0.0.1", port);
        server.Start();
        client.DefaultRequestHeaders.Add(McpDefaults.AuthHeaderName, server.AuthToken);
        var origin = $"http://127.0.0.1:{port}";
        try
        {
            static StringContent Rpc(string method, JsonObject? ps = null) =>
                new(
                    JsonSerializer.Serialize(new
                    {
                        jsonrpc = "2.0",
                        method,
                        id = 1,
                        @params = (object?)ps,
                    }),
                    Encoding.UTF8,
                    "application/json");

            var listResponse = await client.PostAsync(origin + "/mcp", Rpc("resources/list"));
            Assert.Equal(System.Net.HttpStatusCode.OK, listResponse.StatusCode);
            var listBody = await listResponse.Content.ReadAsStringAsync();
            Assert.Contains("temno://models/video", listBody);
            Assert.Contains("temno://models/image", listBody);
            Assert.Contains("palmier://models/video", listBody);
            Assert.Contains("palmier://models/image", listBody);

            foreach (var uri in new[] { "temno://models/video", "palmier://models/video" })
            {
                var readResponse = await client.PostAsync(
                    origin + "/mcp",
                    Rpc("resources/read", new JsonObject { ["uri"] = uri }));
                Assert.Equal(System.Net.HttpStatusCode.OK, readResponse.StatusCode);
                Assert.Contains(uri, await readResponse.Content.ReadAsStringAsync());
            }
        }
        finally
        {
            server.Stop();
        }
    }
}
