using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Temno.Core.IO;

namespace Temno.Mcp;

/// <summary>
/// User-local MCP service prefs. Path: %LocalAppData%\Temno\mcp-preferences.json
/// Mac: UserDefaults <c>io.palmier.pro.mcp.enabled</c> (default on). Windows also persists the POST token
/// so Cursor/Claude/Codex/Grok/Gemini configs survive app restarts.
/// </summary>
public sealed class McpPreferences
{
    public bool Enabled { get; set; } = true;
    public string AuthToken { get; set; } = "";

    public static string DefaultPath =>
        Path.Combine(
            UserDataRoots.LocalAppDataRoot,
            "mcp-preferences.json");

    public static string NewAuthToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static McpPreferences Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        FileDto? dto = null;
        try
        {
            if (File.Exists(path))
                dto = JsonSerializer.Deserialize<FileDto>(File.ReadAllText(path), JsonOptions);
        }
        catch
        {
            dto = null;
        }

        var prefs = new McpPreferences
        {
            Enabled = dto?.Enabled ?? true,
            AuthToken = dto?.AuthToken?.Trim() ?? "",
        };
        if (!LooksLikeToken(prefs.AuthToken))
            prefs.AuthToken = NewAuthToken();
        return prefs;
    }

    public static McpPreferences LoadOrCreate(string? path = null)
    {
        var file = path ?? DefaultPath;
        var previousToken = TryReadToken(file);
        var prefs = Load(file);
        if (previousToken is null || !string.Equals(previousToken, prefs.AuthToken, StringComparison.Ordinal))
            Save(prefs, file);
        return prefs;
    }

    public static void Save(McpPreferences prefs, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(prefs);
        if (!LooksLikeToken(prefs.AuthToken))
            prefs.AuthToken = NewAuthToken();

        var file = path ?? DefaultPath;
        var dir = Path.GetDirectoryName(file);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(
            new FileDto { Enabled = prefs.Enabled, AuthToken = prefs.AuthToken },
            JsonOptions);
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, file, overwrite: true);
    }

    public static bool LooksLikeToken(string? token)
        => !string.IsNullOrWhiteSpace(token)
           && token.Length >= 32
           && token.All(Uri.IsHexDigit);

    private static string? TryReadToken(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            var dto = JsonSerializer.Deserialize<FileDto>(File.ReadAllText(path), JsonOptions);
            var token = dto?.AuthToken?.Trim();
            return LooksLikeToken(token) ? token : null;
        }
        catch
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private sealed class FileDto
    {
        [JsonPropertyName("enabled")]
        public bool? Enabled { get; set; }

        [JsonPropertyName("authToken")]
        public string? AuthToken { get; set; }
    }
}
