namespace Temno.Core.IO;

/// <summary>
/// Dialog-facing snapshot of <see cref="UserPreferences"/> fields that Settings writes.
/// Does not touch MCP tokens (those stay in mcp-preferences.json). Hosted gateway, Supabase URL,
/// anon key and OAuth redirect are deliberately absent: they resolve from the environment and
/// compiled defaults only (<see cref="Temno.Core.Account.HostedEndpoints"/>).
/// </summary>
public sealed class SettingsDraft
{
    public string AgentBaseUrl { get; set; } = AgentPrefs.DefaultBaseUrl;
    public string AgentApiKey { get; set; } = "";
    public string AgentModel { get; set; } = AgentPrefs.DefaultModel;
    /// <summary>WP-422: the hosted model new projects start on; empty is the default model.</summary>
    public string AgentNewChatModel { get; set; } = "";
    /// <summary>The effort a new project's first chat starts at; empty is High.</summary>
    public string AgentNewChatEffort { get; set; } = "";
    public string GenerationBaseUrl { get; set; } = "";
    public string GenerationApiKey { get; set; } = "";
    public bool TelemetryEnabled { get; set; }
    public bool PreferOnnx { get; set; } = true;
    public string OnnxModelPath { get; set; } = "";
    public string UpdateFeedUrl { get; set; } = "";
    public bool PreferHardwareEncode { get; set; } = true;
    public bool AgentPreferByok { get; set; }
}

public static class SettingsPrefsMapper
{
    public static SettingsDraft FromPreferences(UserPreferences prefs)
    {
        ArgumentNullException.ThrowIfNull(prefs);
        return new SettingsDraft
        {
            AgentBaseUrl = prefs.Agent.BaseUrl,
            AgentApiKey = prefs.Agent.ApiKey,
            AgentModel = prefs.Agent.Model,
            AgentNewChatModel = prefs.Agent.NewChatModel ?? "",
            AgentNewChatEffort = prefs.Agent.NewChatEffort ?? "",
            GenerationBaseUrl = prefs.Generation.BaseUrl,
            GenerationApiKey = prefs.Generation.ApiKey,
            TelemetryEnabled = prefs.Telemetry.Enabled,
            PreferOnnx = prefs.Search.PreferOnnx,
            OnnxModelPath = prefs.Search.OnnxModelPath,
            UpdateFeedUrl = prefs.Updates.FeedUrl,
            PreferHardwareEncode = prefs.Export.PreferHardwareEncode,
            AgentPreferByok = prefs.Agent.PreferByok,
        };
    }

    public static void Apply(SettingsDraft draft, UserPreferences prefs)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(prefs);
        prefs.Agent.BaseUrl = draft.AgentBaseUrl.Trim();
        prefs.Agent.ApiKey = draft.AgentApiKey.Trim();
        prefs.Agent.Model = string.IsNullOrWhiteSpace(draft.AgentModel)
            ? AgentPrefs.DefaultModel
            : draft.AgentModel.Trim();
        prefs.Agent.NewChatModel = draft.AgentNewChatModel?.Trim() ?? "";
        prefs.Agent.NewChatEffort = draft.AgentNewChatEffort?.Trim() ?? "";
        prefs.Generation.BaseUrl = draft.GenerationBaseUrl.Trim();
        prefs.Generation.ApiKey = draft.GenerationApiKey.Trim();
        prefs.Telemetry.Enabled = draft.TelemetryEnabled;
        prefs.Search.PreferOnnx = draft.PreferOnnx;
        prefs.Search.OnnxModelPath = draft.OnnxModelPath.Trim();
        prefs.Updates.FeedUrl = draft.UpdateFeedUrl.Trim();
        prefs.Export.PreferHardwareEncode = draft.PreferHardwareEncode;
        prefs.Agent.PreferByok = draft.AgentPreferByok;
    }

    public static void Save(SettingsDraft draft, string? path = null)
    {
        var prefs = UserPreferences.Load(path);
        Apply(draft, prefs);
        UserPreferences.Save(prefs, path);
    }
}
