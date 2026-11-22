using Temno.Core.Account;
using Temno.Core.IO;
using Temno.Environments;
using Xunit;

namespace Temno.Core.Tests;

// WP-334: Temno-Dev and every development build keep their data in their own root, apart from the production app's.
public sealed class DevelopmentDataRootTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "temno-devroot-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    private string Shared => Path.Combine(_root, "Temno");
    private string Dev => Path.Combine(_root, "Temno-Dev");

    private void Write(string root, string relative, string text)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public void A_development_build_keeps_its_own_root()
    {
        Assert.Equal(TemnoEnvironment.Development, HostedEndpoints.BuildEnvironment);
        Assert.Equal("Temno-Dev", Path.GetFileName(UserDataRoots.LocalAppDataRoot));
        Assert.Equal("Temno", Path.GetFileName(UserDataRoots.SharedLocalAppDataRoot));
    }

    [Fact]
    public void Seeding_copies_ui_state_moves_development_sign_ins_and_leaves_tokens_and_production()
    {
        Write(Shared, "development-backend.json", "{\"environment\":\"development\"}");
        Write(Shared, "user-preferences.json", "prefs");
        Write(Shared, "editor-layout.json", "layout");
        Write(Shared, "mcp-preferences.json", "production token");
        Write(Shared, Path.Combine("account", "devkey", "session.dpapi"), "dev session");
        Write(Shared, Path.Combine("account", "prodkey", "session.dpapi"), "prod session");

        Assert.True(UserDataMigration.SeedDevelopmentRoot(Shared, Dev, "prodkey"));

        Assert.Equal("prefs", File.ReadAllText(Path.Combine(Dev, "user-preferences.json")));
        Assert.Equal("layout", File.ReadAllText(Path.Combine(Dev, "editor-layout.json")));
        Assert.True(File.Exists(Path.Combine(Dev, "development-backend.json")));
        Assert.True(File.Exists(Path.Combine(Shared, "user-preferences.json")), "copied, not moved");
        // A sign-in exists in one place only: a refresh token used from two places revokes the session.
        Assert.Equal("dev session", File.ReadAllText(Path.Combine(Dev, "account", "devkey", "session.dpapi")));
        Assert.False(Directory.Exists(Path.Combine(Shared, "account", "devkey")));
        Assert.Equal("prod session", File.ReadAllText(Path.Combine(Shared, "account", "prodkey", "session.dpapi")));
        Assert.False(Directory.Exists(Path.Combine(Dev, "account", "prodkey")));
        // Temno-Dev mints its own MCP token.
        Assert.False(File.Exists(Path.Combine(Dev, "mcp-preferences.json")));
    }

    [Fact]
    public void Seeding_never_overwrites_what_the_development_root_already_has()
    {
        Write(Shared, "user-preferences.json", "shared");
        Write(Dev, "user-preferences.json", "own");
        Write(Shared, Path.Combine("account", "devkey", "session.dpapi"), "older");
        Write(Dev, Path.Combine("account", "devkey", "session.dpapi"), "newer");

        Assert.False(UserDataMigration.SeedDevelopmentRoot(Shared, Dev, "prodkey"));
        Assert.Equal("own", File.ReadAllText(Path.Combine(Dev, "user-preferences.json")));
        Assert.Equal("newer", File.ReadAllText(Path.Combine(Dev, "account", "devkey", "session.dpapi")));
        Assert.True(File.Exists(Path.Combine(Shared, "account", "devkey", "session.dpapi")));
        Assert.False(UserDataMigration.SeedDevelopmentRoot(Shared, Shared, "prodkey"));
    }
}
