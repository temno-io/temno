using Temno.Core.Account;
using Temno.Core.IO;
using Temno.Environments;
using Xunit;

namespace Temno.Core.Tests;

[Collection("EnvRegistry")]
public sealed class UserPreferencesTests : IDisposable
{
    private readonly string _file;
    private readonly string? _prevAnthropic;
    private readonly string? _prevGenKey;
    private readonly string? _prevGenUrl;

    public UserPreferencesTests()
    {
        _file = Path.Combine(Path.GetTempPath(), "temno-prefs-" + Guid.NewGuid().ToString("N") + ".json");
        UserPreferences.SetPathForTests(_file);
        _prevAnthropic = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        _prevGenKey = Environment.GetEnvironmentVariable("TEMNO_API_KEY");
        _prevGenUrl = Environment.GetEnvironmentVariable("TEMNO_API_BASE_URL");
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
        Environment.SetEnvironmentVariable("TEMNO_ANTHROPIC_API_KEY", null);
        Environment.SetEnvironmentVariable("TEMNO_ANTHROPIC_BASE_URL", null);
        Environment.SetEnvironmentVariable("TEMNO_API_KEY", null);
        Environment.SetEnvironmentVariable("TEMNO_API_BASE_URL", null);
        Environment.SetEnvironmentVariable("TEMNO_SIGLIP_MODEL", null);
        Environment.SetEnvironmentVariable("TEMNO_UPDATE_FEED_URL", null);
        Environment.SetEnvironmentVariable("TEMNO_GATEWAY_URL", null);
        Environment.SetEnvironmentVariable("TEMNO_SUPABASE_ANON_KEY", null);
    }

    public void Dispose()
    {
        UserPreferences.SetPathForTests(null);
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", _prevAnthropic);
        Environment.SetEnvironmentVariable("TEMNO_API_KEY", _prevGenKey);
        Environment.SetEnvironmentVariable("TEMNO_API_BASE_URL", _prevGenUrl);
        Environment.SetEnvironmentVariable("TEMNO_ANTHROPIC_API_KEY", null);
        Environment.SetEnvironmentVariable("TEMNO_ANTHROPIC_BASE_URL", null);
        Environment.SetEnvironmentVariable("TEMNO_SIGLIP_MODEL", null);
        Environment.SetEnvironmentVariable("TEMNO_UPDATE_FEED_URL", null);
        try { File.Delete(_file); } catch { /* ignore */ }
    }

    [Fact]
    public void RoundTrip_PreservesAgentAndGeneration()
    {
        var prefs = new UserPreferences
        {
            Agent = { ApiKey = "sk-ant-test", Model = "claude-haiku-4-5-20251001" },
            Generation = { ApiKey = "gen-key", BaseUrl = "https://example.test" },
            Telemetry = { Enabled = true },
        };
        UserPreferences.Save(prefs);
        var loaded = UserPreferences.Load();
        Assert.Equal("sk-ant-test", loaded.Agent.ApiKey);
        Assert.Equal("https://example.test", loaded.ResolveGenerationBaseUrl());
        Assert.True(loaded.TelemetryEnabled);
    }

    [Fact]
    public void Env_OverlaysFile()
    {
        UserPreferences.Save(new UserPreferences { Agent = { ApiKey = "file-key" } });
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "env-key");
        var loaded = UserPreferences.Load();
        Assert.Equal("env-key", loaded.ResolveAgentApiKey());
        Assert.Equal("file-key", loaded.Agent.ApiKey);
    }

    [Fact]
    public void MissingFile_ReturnsDefaults()
    {
        var loaded = UserPreferences.Load();
        Assert.Equal(AgentPrefs.DefaultBaseUrl, loaded.ResolveAgentBaseUrl());
        Assert.Equal("", loaded.ResolveGenerationApiKey());
        Assert.False(loaded.TelemetryEnabled);
        Assert.Null(loaded.ResolveUpdateFeedUrl());
    }

    [Fact]
    public void SettingsDraft_RoundTrip_PersistsKeysTelemetryAndFeed()
    {
        var draft = new SettingsDraft
        {
            AgentBaseUrl = AgentPrefs.DefaultBaseUrl,
            AgentApiKey = "sk-ant-settings",
            AgentModel = "claude-sonnet-5",
            GenerationBaseUrl = "",
            GenerationApiKey = "gen-key",
            TelemetryEnabled = false,
            PreferOnnx = true,
            OnnxModelPath = @"D:\models\siglip2-base-patch16-256.onnx",
            UpdateFeedUrl = "https://example.test/palmier/feed.json",
            PreferHardwareEncode = false,
            AgentPreferByok = true,
        };
        SettingsPrefsMapper.Save(draft);
        var loaded = UserPreferences.Load();
        Assert.Equal("sk-ant-settings", loaded.Agent.ApiKey);
        Assert.Equal("claude-sonnet-5", loaded.Agent.Model);
        Assert.Equal("", loaded.Generation.BaseUrl);
        Assert.Equal("gen-key", loaded.ResolveGenerationApiKey());
        Assert.False(loaded.TelemetryEnabled);
        Assert.True(loaded.Search.PreferOnnx);
        Assert.Equal(@"D:\models\siglip2-base-patch16-256.onnx", loaded.Search.OnnxModelPath);
        Assert.Equal("https://example.test/palmier/feed.json", loaded.Updates.FeedUrl);
        Assert.Equal("https://example.test/palmier/feed.json", loaded.ResolveUpdateFeedUrl());
        Assert.False(loaded.Export.PreferHardwareEncode);
        Assert.False(loaded.ResolvePreferHardwareEncode());
        Assert.True(loaded.Agent.PreferByok);
        var json = File.ReadAllText(_file);
        Assert.DoesNotContain("access_token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("refresh_token", json, StringComparison.OrdinalIgnoreCase);
        // Hosted gateway / Supabase / anon key / redirect are product infrastructure, not settings:
        // Settings Save must write none of them (owner-directed removal, 2026-09-12).
        Assert.DoesNotContain("hosted", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("gateway", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("supabase", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("anon", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("redirect", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SettingsSave_KeepsStoredSearchEngineValues()
    {
        // WP-397: Settings → Search has no engine controls, so a Save made for any other field must
        // write back exactly the search values it loaded, including a non-default path and false.
        var prefs = UserPreferences.Load();
        prefs.Search.PreferOnnx = false;
        prefs.Search.OnnxModelPath = @"D:\models\custom-siglip.onnx";
        UserPreferences.Save(prefs);

        var draft = SettingsPrefsMapper.FromPreferences(UserPreferences.Load());
        draft.AgentModel = "claude-sonnet-5";
        SettingsPrefsMapper.Save(draft);

        var loaded = UserPreferences.Load();
        Assert.False(loaded.Search.PreferOnnx);
        Assert.Equal(@"D:\models\custom-siglip.onnx", loaded.Search.OnnxModelPath);
        Assert.Equal("claude-sonnet-5", loaded.Agent.Model);
    }

    [Fact]
    public void HostedEndpoints_IgnorePrefsFile_EnvThenDeveloperFileOnly()
    {
        // A prefs file written by an older build (or by hand) that still carries hosted
        // endpoints must not steer where the signed-in JWT is sent. WP-260: there is no compiled
        // development default any more, so with no variables and no developer file this
        // development test build is unconfigured.
        File.WriteAllText(_file, """
            {
              "agent": { "preferByok": true },
              "hosted": {
                "gatewayUrl": "https://evil.example.test",
                "supabaseUrl": "https://evil.supabase.example.test",
                "anonKey": "stale-anon",
                "redirectUri": "http://127.0.0.1:1/steal"
              }
            }
            """);
        var loaded = UserPreferences.Load();
        Assert.True(loaded.Agent.PreferByok); // the rest of the file still loads

        var developerFile = Path.Combine(Path.GetTempPath(), "temno-prefs-devfile-" + Guid.NewGuid().ToString("N") + ".json");
        DevelopmentBackendFile.SetPathForTests(developerFile);
        Environment.SetEnvironmentVariable("TEMNO_SUPABASE_URL", null);
        Environment.SetEnvironmentVariable("TEMNO_ACCOUNTS_URL", null);
        Environment.SetEnvironmentVariable("TEMNO_AUTH_REDIRECT_URI", null);
        try
        {
            Assert.Equal(TemnoEnvironment.Development, HostedEndpoints.BuildEnvironment);
            Assert.Equal("", HostedEndpoints.ResolveGatewayUrl());
            Assert.False(HostedEndpoints.GatewayConfigured());
            Assert.Equal("", HostedEndpoints.ResolveAnonKey());
            Assert.Equal("", HostedEndpoints.ResolveSupabaseUrl());
            Assert.False(HostedEndpoints.AuthConfigured());
            Assert.False(HostedEndpoints.AccountsConfigured());
            Assert.Equal(HostedEndpoints.DefaultRedirectUri, HostedEndpoints.ResolveRedirectUri());
            Assert.Equal(HostedEndpoints.DevelopmentNotConfiguredMessage, HostedEndpoints.NotConfiguredMessage);

            File.WriteAllText(developerFile, """
                {"environment":"development","supabaseUrl":"https://devref.supabase.co/","supabaseAnonKey":"sb_publishable_devfile","gatewayUrl":"https://gateway.dev.test","accountsUrl":"https://accounts.dev.test"}
                """);
            Assert.Equal("https://devref.supabase.co", HostedEndpoints.ResolveSupabaseUrl());
            Assert.Equal("sb_publishable_devfile", HostedEndpoints.ResolveAnonKey());
            Assert.Equal("https://gateway.dev.test", HostedEndpoints.ResolveGatewayUrl());
            Assert.Equal("https://accounts.dev.test", HostedEndpoints.ResolveAccountsUrl());
            Assert.True(HostedEndpoints.AuthConfigured());
            Assert.True(HostedEndpoints.AccountsConfigured());
            Assert.Null(HostedEndpoints.Current.Problem);

            Environment.SetEnvironmentVariable("TEMNO_GATEWAY_URL", "https://gateway.example.test/");
            Environment.SetEnvironmentVariable("TEMNO_SUPABASE_ANON_KEY", "env-anon");
            Assert.Equal("https://gateway.example.test", HostedEndpoints.ResolveGatewayUrl());
            Assert.Equal("env-anon", HostedEndpoints.ResolveAnonKey());
            Assert.Equal("https://devref.supabase.co", HostedEndpoints.ResolveSupabaseUrl());
            Assert.True(HostedEndpoints.AuthConfigured());
        }
        finally
        {
            Environment.SetEnvironmentVariable("TEMNO_GATEWAY_URL", null);
            Environment.SetEnvironmentVariable("TEMNO_SUPABASE_ANON_KEY", null);
            DevelopmentBackendFile.SetPathForTests(null);
            try { File.Delete(developerFile); } catch { /* ignore */ }
        }

        // Re-saving through Settings drops the stale block instead of carrying it forward.
        SettingsPrefsMapper.Save(SettingsPrefsMapper.FromPreferences(loaded));
        var rewritten = File.ReadAllText(_file);
        Assert.DoesNotContain("hosted", rewritten, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("evil", rewritten, StringComparison.OrdinalIgnoreCase);
    }

    // WP-402 D-1: a build from source and the installed Temno share this file, so Settings → Save in one must not erase a
    // setting only the other knows.
    [Fact]
    public void SettingsSave_KeepsUnknownTopLevelAndNestedFields()
    {
        File.WriteAllText(_file, """
            {
              "agent": { "model": "claude-sonnet-5", "forkAgentMode": "plan", "forkLimits": { "turns": 12, "tools": ["a", "b"] } },
              "export": { "preferHardwareEncode": true, "forkPreset": null },
              "forkPanel": { "dock": "left", "width": 312.5, "pinned": true },
              "forkFlags": [1, "two", false]
            }
            """);

        var draft = SettingsPrefsMapper.FromPreferences(UserPreferences.Load());
        draft.AgentModel = "claude-opus-5";
        draft.PreferHardwareEncode = false;
        SettingsPrefsMapper.Save(draft);

        using var saved = System.Text.Json.JsonDocument.Parse(File.ReadAllText(_file));
        var root = saved.RootElement;
        var panel = root.GetProperty("forkPanel");
        Assert.Equal("left", panel.GetProperty("dock").GetString());
        Assert.Equal(312.5, panel.GetProperty("width").GetDouble());
        Assert.True(panel.GetProperty("pinned").GetBoolean());
        Assert.Equal(3, root.GetProperty("forkFlags").GetArrayLength());
        Assert.Equal("two", root.GetProperty("forkFlags")[1].GetString());
        var agent = root.GetProperty("agent");
        Assert.Equal("claude-opus-5", agent.GetProperty("model").GetString());
        Assert.Equal("plan", agent.GetProperty("forkAgentMode").GetString());
        Assert.Equal(12, agent.GetProperty("forkLimits").GetProperty("turns").GetInt32());
        Assert.Equal(2, agent.GetProperty("forkLimits").GetProperty("tools").GetArrayLength());
        var export = root.GetProperty("export");
        Assert.False(export.GetProperty("preferHardwareEncode").GetBoolean());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, export.GetProperty("forkPreset").ValueKind);

        // Saving again changes nothing, and no key is written twice.
        var once = File.ReadAllText(_file);
        SettingsPrefsMapper.Save(SettingsPrefsMapper.FromPreferences(UserPreferences.Load()));
        Assert.Equal(once, File.ReadAllText(_file));
        foreach (var key in new[] { "\"agent\"", "\"forkPanel\"", "\"preferSiglip\"", "\"telemetryEnabled\"" })
            Assert.True(once.Split(key).Length <= 2, key + " is written more than once");
    }

    // WP-422: the hosted model new chats start on.
    [Fact]
    public void NewChatModel_RoundTrips_AndAMissingFieldReadsAsTemnosDefault()
    {
        File.WriteAllText(_file, """{ "agent": { "model": "claude-sonnet-5" } }""");
        var loaded = UserPreferences.Load();
        Assert.Equal("", loaded.Agent.NewChatModel);
        Assert.Equal("", SettingsPrefsMapper.FromPreferences(loaded).AgentNewChatModel);

        var draft = SettingsPrefsMapper.FromPreferences(loaded);
        draft.AgentNewChatModel = "  claude-sonnet-5.5 ";
        SettingsPrefsMapper.Save(draft);
        Assert.Equal("claude-sonnet-5.5", UserPreferences.Load().Agent.NewChatModel);
        // The own-key model is a different field and is untouched by it.
        Assert.Equal("claude-sonnet-5", UserPreferences.Load().Agent.Model);
        using (var saved = System.Text.Json.JsonDocument.Parse(File.ReadAllText(_file)))
            Assert.Equal("claude-sonnet-5.5", saved.RootElement.GetProperty("agent").GetProperty("newChatModel").GetString());

        draft.AgentNewChatModel = "";
        SettingsPrefsMapper.Save(draft);
        Assert.Equal("", UserPreferences.Load().Agent.NewChatModel);
    }

    // WP-422 follow-up: the reasoning effort a new project's first chat starts at.
    [Fact]
    public void NewChatEffort_RoundTrips_AndAMissingFieldReadsAsEmpty()
    {
        File.WriteAllText(_file, """{ "agent": { "newChatModel": "claude-sonnet-5.5" } }""");
        Assert.Equal("", UserPreferences.Load().Agent.NewChatEffort);
        var draft = SettingsPrefsMapper.FromPreferences(UserPreferences.Load());
        Assert.Equal("", draft.AgentNewChatEffort);

        draft.AgentNewChatEffort = " max ";
        SettingsPrefsMapper.Save(draft);

        Assert.Equal("max", UserPreferences.Load().Agent.NewChatEffort);
        using var saved = System.Text.Json.JsonDocument.Parse(File.ReadAllText(_file));
        var agent = saved.RootElement.GetProperty("agent");
        Assert.Equal("max", agent.GetProperty("newChatEffort").GetString());
        Assert.Equal("claude-sonnet-5.5", agent.GetProperty("newChatModel").GetString());
    }

    // WP-422: a build from source and the installed Temno share this file; one without the field must keep it on Save.
    // AgentPrefs before WP-422, reduced to the shape the file round-trip depends on: the extension-data bag.
    private sealed class AgentPrefsWithoutNewChatModel
    {
        [System.Text.Json.Serialization.JsonExtensionData]
        public Dictionary<string, System.Text.Json.JsonElement>? ExtensionData { get; set; }
        public string Model { get; set; } = "";
    }

    [Fact]
    public void A_build_without_the_new_chat_model_field_keeps_it_through_Save()
    {
        var options = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };
        var draft = SettingsPrefsMapper.FromPreferences(UserPreferences.Load());
        draft.AgentNewChatModel = "claude-sonnet-5.5";
        SettingsPrefsMapper.Save(draft);
        var agentJson = System.Text.Json.JsonDocument.Parse(File.ReadAllText(_file)).RootElement.GetProperty("agent").GetRawText();

        var older = System.Text.Json.JsonSerializer.Deserialize<AgentPrefsWithoutNewChatModel>(agentJson, options)!;
        older.Model = "claude-opus-5";
        var rewritten = System.Text.Json.JsonSerializer.Serialize(older, options);

        using var after = System.Text.Json.JsonDocument.Parse(rewritten);
        Assert.Equal("claude-sonnet-5.5", after.RootElement.GetProperty("newChatModel").GetString());
        Assert.Equal("claude-opus-5", after.RootElement.GetProperty("model").GetString());
    }

    // WP-402 D-1: another build's writer is now part of this file's input.
    [Fact]
    public void A_null_section_reads_as_its_defaults_and_saves_as_one()
    {
        File.WriteAllText(_file, """{ "agent": null, "export": null, "forkTop": 1 }""");
        var loaded = UserPreferences.Load();
        Assert.Equal(AgentPrefs.DefaultModel, loaded.Agent.Model);
        Assert.True(loaded.Export.PreferHardwareEncode);
        SettingsPrefsMapper.Save(SettingsPrefsMapper.FromPreferences(loaded));
        using var saved = System.Text.Json.JsonDocument.Parse(File.ReadAllText(_file));
        Assert.Equal(System.Text.Json.JsonValueKind.Object, saved.RootElement.GetProperty("agent").ValueKind);
        Assert.Equal(1, saved.RootElement.GetProperty("forkTop").GetInt32());
    }

    [Fact]
    public void A_file_this_build_cannot_read_is_kept_before_a_save_replaces_it()
    {
        // Another build changed a known field's type: this build reads the defaults, and its next save would
        // replace everything the file held.
        const string other = """{ "telemetry": { "enabled": "ask" }, "forkTop": { "keep": "me" }, "agent": { "apiKey": "sk-keep" } }""";
        File.WriteAllText(_file, other);
        var copy = UserPreferences.UnreadableCopyPath(_file);
        try
        {
            Assert.Equal("", UserPreferences.Load().Agent.ApiKey);
            PreferencesSectionStoreSave();
            Assert.Equal(other, File.ReadAllText(copy));
            Assert.Contains("\"agent\"", File.ReadAllText(_file));

            // A readable file is never copied.
            File.Delete(copy);
            SettingsPrefsMapper.Save(SettingsPrefsMapper.FromPreferences(UserPreferences.Load()));
            Assert.False(File.Exists(copy));
        }
        finally
        {
            try { File.Delete(copy); } catch { /* ignore */ }
        }
    }

    private static void PreferencesSectionStoreSave()
    {
        var prefs = UserPreferences.Load();
        prefs.EditorSections["captions.style"] = false;
        UserPreferences.Save(prefs);
    }

    [Fact]
    public void PreferHardwareEncode_DefaultsTrue_AndEnvOverlays()
    {
        var loaded = UserPreferences.Load();
        Assert.True(loaded.ResolvePreferHardwareEncode());

        Environment.SetEnvironmentVariable("TEMNO_PREFER_HARDWARE_ENCODE", "false");
        try
        {
            Assert.False(loaded.ResolvePreferHardwareEncode());
        }
        finally
        {
            Environment.SetEnvironmentVariable("TEMNO_PREFER_HARDWARE_ENCODE", null);
        }
    }
}
