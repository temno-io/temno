using System.Text.Json;
using System.Text.Json.Serialization;
using Temno.Core.Account;
using Temno.Core.Preview;
using Temno.Environments;

namespace Temno.Core.IO;

/// <summary>
/// User-local app prefs (not MCP token, not project package).
/// Path: %LocalAppData%\Temno\user-preferences.json
/// Env vars overlay file values when set (see Resolve* helpers).
/// A build from source and the installed Temno share this file (WP-402 D-1), so fields this build does not know are kept
/// through a Save, at the top level and in each section. Only keys this build retired on purpose are dropped.
/// </summary>
public sealed class UserPreferences
{
    /// <summary>Fields this build does not know, kept so a Save by another build does not erase them (WP-402 D-1).</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    // Another build's writer may write a section as null (WP-402 D-1); it reads as the defaults.
    public AgentPrefs Agent { get => _agent; set => _agent = value ?? new(); }
    public GenerationPrefs Generation { get => _generation; set => _generation = value ?? new(); }
    public SearchPrefs Search { get => _search; set => _search = value ?? new(); }
    public UpdatePrefs Updates { get => _updates; set => _updates = value ?? new(); }
    public TelemetryPrefs Telemetry { get => _telemetry; set => _telemetry = value ?? new(); }
    public ExportPrefs Export { get => _export; set => _export = value ?? new(); }
    public PreviewPrefs Preview { get => _preview; set => _preview = value ?? new(); }

    private AgentPrefs _agent = new();
    private GenerationPrefs _generation = new();
    private SearchPrefs _search = new();
    private UpdatePrefs _updates = new();
    private TelemetryPrefs _telemetry = new();
    private ExportPrefs _export = new();
    private PreviewPrefs _preview = new();

    /// <summary>
    /// Remembered expand/collapse for editor <c>SectionHeader</c>s, keyed by section id
    /// (e.g. <c>captions.style</c>). Empty means "use each header's fallback".
    /// </summary>
    public Dictionary<string, bool> EditorSections
    {
        get => _editorSections;
        set => _editorSections = value ?? new(StringComparer.Ordinal);
    }

    private Dictionary<string, bool> _editorSections = new(StringComparer.Ordinal);

    /// <summary>
    /// Top-level keys this build retired, dropped rather than carried forward: hosted endpoints are product
    /// infrastructure, never settings (owner-directed removal, 2026-09-12; <see cref="HostedEndpoints"/>).
    /// </summary>
    private static readonly string[] RetiredKeys = ["hosted"];

    private static string? _pathOverride;

    public static void SetPathForTests(string? path) => _pathOverride = path;

    public static string DefaultPath =>
        _pathOverride ?? Path.Combine(
            UserDataRoots.LocalAppDataRoot,
            "user-preferences.json");

    public static UserPreferences Load(string? path = null)
    {
        var file = path ?? DefaultPath;
        try
        {
            if (File.Exists(file))
            {
                var loaded = JsonSerializer.Deserialize<UserPreferences>(File.ReadAllText(file), JsonOptions);
                if (loaded is not null)
                {
                    loaded.DropRetiredKeys();
                    return loaded;
                }
            }
        }
        catch
        {
            // Corrupt file → defaults.
        }

        return new UserPreferences();
    }

    public static void Save(UserPreferences prefs, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(prefs);
        var file = path ?? DefaultPath;
        var dir = Path.GetDirectoryName(file);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        prefs.DropRetiredKeys();
        KeepUnreadableCopy(file);
        var json = JsonSerializer.Serialize(prefs, JsonOptions);
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, file, overwrite: true);
    }

    /// <summary>The copy kept of a file this build could not read, before a Save replaces it (WP-402 D-1).</summary>
    public static string UnreadableCopyPath(string file) => file + ".unreadable";

    /// <summary>
    /// A file this build cannot read (another build changed a field's type, or it is damaged) loads as the defaults,
    /// and the next Save would replace it. A copy is kept first, so nothing the other build wrote is lost for good.
    /// </summary>
    private static void KeepUnreadableCopy(string file)
    {
        try
        {
            if (!File.Exists(file))
                return;
            var text = File.ReadAllText(file);
            if (text.Trim().Length == 0)
                return;
            try
            {
                if (JsonSerializer.Deserialize<UserPreferences>(text, JsonOptions) is not null)
                    return;
            }
            catch (JsonException)
            {
                // Unreadable: keep it below.
            }

            File.Copy(file, UnreadableCopyPath(file), overwrite: true);
        }
        catch (IOException)
        {
            // Best effort; the Save itself still runs.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void DropRetiredKeys()
    {
        if (ExtensionData is null)
            return;
        foreach (var key in ExtensionData.Keys.Where(k => RetiredKeys.Contains(k, StringComparer.OrdinalIgnoreCase)).ToArray())
            ExtensionData.Remove(key);
    }

    public string ResolveAgentApiKey()
        => FirstNonEmpty(
            Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"),
            EnvCompat.Get("TEMNO_ANTHROPIC_API_KEY"),
            Agent.ApiKey);

    public string ResolveAgentBaseUrl()
    {
        var url = FirstNonEmpty(
            EnvCompat.Get("TEMNO_ANTHROPIC_BASE_URL"),
            Agent.BaseUrl);
        return string.IsNullOrWhiteSpace(url) ? AgentPrefs.DefaultBaseUrl : url.TrimEnd('/');
    }

    public string ResolveAgentModel()
        => FirstNonEmpty(EnvCompat.Get("TEMNO_ANTHROPIC_MODEL"), Agent.Model)
           is { Length: > 0 } m
            ? m
            : AgentPrefs.DefaultModel;

    public string ResolveGenerationApiKey()
        => FirstNonEmpty(EnvCompat.Get("TEMNO_API_KEY"), Generation.ApiKey);

    public string ResolveGenerationBaseUrl()
        => FirstNonEmpty(
            EnvCompat.Get("TEMNO_API_BASE_URL"),
            Generation.BaseUrl);

    public string? ResolveSiglipModelPath()
        => EmptyToNull(FirstNonEmpty(
            EnvCompat.Get("TEMNO_SIGLIP_MODEL"),
            Search.OnnxModelPath));

    /// <summary>
    /// The feed the update check reads: <c>TEMNO_UPDATE_FEED_URL</c>, then the Settings field, then the build's
    /// own (WP-375, <see cref="HostedEndpoints.ResolveUpdateFeedUrl"/>). A production build shows no field
    /// (WP-376), so a value saved there before is not read: nobody could see or clear it.
    /// </summary>
    public string? ResolveUpdateFeedUrl()
        => ResolveUpdateFeedUrl(HostedEndpoints.ResolveUpdateFeedUrl(), HostedEndpoints.BuildEnvironment);

    public string? ResolveUpdateFeedUrl(string? buildFeedUrl, TemnoEnvironment environment)
        => EmptyToNull(FirstNonEmpty(
            EnvCompat.Get("TEMNO_UPDATE_FEED_URL"),
            environment == TemnoEnvironment.Development ? Updates.FeedUrl : null,
            buildFeedUrl));

    public bool PreferSiglip
        => Search.PreferOnnx;

    public bool ResolvePreferHardwareEncode()
    {
        var env = EnvCompat.Get("TEMNO_PREFER_HARDWARE_ENCODE");
        if (bool.TryParse(env, out var parsed))
            return parsed;
        return Export.PreferHardwareEncode;
    }

    public bool TelemetryEnabled
        => Telemetry.Enabled;

    public PreviewResolution ResolvePreviewResolution()
        => PreviewResolution.Parse(Preview.Resolution);

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v))
                return v.Trim();
        }

        return "";
    }

    private static string? EmptyToNull(string value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

public sealed class AgentPrefs
{
    /// <summary>Fields this build does not know, kept so a Save by another build does not erase them (WP-402 D-1).</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    public const string DefaultBaseUrl = "https://api.anthropic.com";
    public const string DefaultModel = "claude-sonnet-5";

    public string BaseUrl { get; set; } = DefaultBaseUrl;
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = DefaultModel;

    /// <summary>
    /// WP-422: the hosted model ID a new project's first chat starts on (<c>HostedAgentProfile.Id</c>, not the own-key <see cref="Model"/>
    /// above). Empty means the default model. A build without this field keeps it through Save in <see cref="ExtensionData"/>.
    /// Not named for "hosted": the preferences tests refuse that word, which would mark a hosted-infrastructure setting.
    /// </summary>
    public string NewChatModel { get; set; } = "";

    /// <summary>
    /// The reasoning effort a new project's first chat starts at on that model (an ID such as <c>high</c>). Empty means
    /// High, or the model's own default when it offers no High (<c>HostedModelNames.StartingEffort</c>).
    /// </summary>
    public string NewChatEffort { get; set; } = "";

    /// <summary>
    /// When true, in-app chat uses the Anthropic BYOK path even if the user is signed in.
    /// Hosted chat is the default once a gateway URL and session exist.
    /// </summary>
    public bool PreferByok { get; set; }
}

public sealed class GenerationPrefs
{
    /// <summary>Fields this build does not know, kept so a Save by another build does not erase them (WP-402 D-1).</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    public string BaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
}

public sealed class SearchPrefs
{
    /// <summary>Fields this build does not know, kept so a Save by another build does not erase them (WP-402 D-1).</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>When true, use ONNX SigLIP if a model file is present; otherwise concept-space fallback.</summary>
    public bool PreferOnnx { get; set; } = true;

    public string OnnxModelPath { get; set; } = "";
}

public sealed class UpdatePrefs
{
    /// <summary>Fields this build does not know, kept so a Save by another build does not erase them (WP-402 D-1).</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>An https or local JSON feed. Empty = the build's own feed, when it has one (WP-375).</summary>
    public string FeedUrl { get; set; } = "";
}

public sealed class TelemetryPrefs
{
    /// <summary>Fields this build does not know, kept so a Save by another build does not erase them (WP-402 D-1).</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>Opt-in. Default off. No Temno SaaS telemetry backend is implied.</summary>
    public bool Enabled { get; set; }
}

public sealed class ExportPrefs
{
    /// <summary>Fields this build does not know, kept so a Save by another build does not erase them (WP-402 D-1).</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>
    /// When true, video export prefers NVENC/QSV/AMF hardware encoders and falls back to
    /// software (libx264/libx265) when the hardware encoder is absent or refuses the job.
    /// TEMNO_PREFER_HARDWARE_ENCODE overlays this value when set.
    /// </summary>
    public bool PreferHardwareEncode { get; set; } = true;
}

public sealed class PreviewPrefs
{
    /// <summary>Fields this build does not know, kept so a Save by another build does not erase them (WP-402 D-1).</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>
    /// Frame preview resolution tag (<see cref="PreviewResolution"/>): "full", "2k", "1080p",
    /// "720p" (default), "540p", "360p", or a custom even max-edge pixel count.
    /// </summary>
    public string Resolution { get; set; } = PreviewResolution.DefaultTag;
}
