using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using Temno.Core.Account;
using Temno.Environments;

namespace Temno.Core.Updates;

public enum UpdateCheckKind
{
    Unconfigured,
    UpToDate,
    UpdateAvailable,
    Error,
}

public sealed class UpdateCheckResult
{
    public required UpdateCheckKind Kind { get; init; }
    public string Message { get; init; } = "";
    public string? FeedVersion { get; init; }
    public string? AppInstallerUrl { get; init; }

    /// <summary>The feed was a local file. Only a local feed may name a local package.</summary>
    public bool FeedIsLocal { get; init; }
}

/// <summary>
/// Optional JSON feed checker. Empty URL never opens a connection.
/// Feed shape: <c>{ "version": "0.1.0", "appInstallerUrl": "https://..." }</c>.
/// Accepts https, <c>file://</c>, or a rooted local path; never http or a network share.
/// </summary>
public sealed class UpdateChecker
{
    public const string UnconfiguredMessage = "No hosted update channel configured.";

    public const string UnreachableMessage = "Could not check for updates: the feed could not be reached.";

    public const string HttpsOnlyMessage = "Could not check for updates: the feed address must start with https://.";

    public const string NetworkShareMessage = "Could not check for updates: Temno doesn't read update feeds from network shares.";

    public const string LeftHttpsMessage = "Could not check for updates: the feed redirected away from https.";

    public const string ProductionFeedMessage =
        "Could not check for updates: a development build never reads production's update feed.";

    private const long MaxFeedBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly HttpMessageHandler? _handler;
    private readonly EnvironmentIdentity? _refused;
    private readonly string? _ownFeed;

    /// <summary>
    /// A development build refuses a feed on any of production's hosts, wherever its URL came from (WP-375). A production
    /// build reads a 404 from its own compiled feed as no release published yet (WP-376).
    /// </summary>
    public UpdateChecker(HttpMessageHandler? handler = null)
        : this(handler,
            HostedEndpoints.BuildEnvironment == TemnoEnvironment.Development ? EnvironmentIdentity.Production : null,
            HostedEndpoints.BuildEnvironment == TemnoEnvironment.Production ? EnvironmentIdentity.Production.UpdateFeedUrl : null)
    {
    }

    /// <param name="handler">The HTTP handler, for tests.</param>
    /// <param name="refusedHosts">The identity whose hosts this build reads no feed from, or null for none.</param>
    /// <param name="ownFeed">
    /// The feed compiled into this build, whose 404 means no release is published yet rather than a fault: Temno ships
    /// before its first release reaches the host (WP-376). Any other feed's 404 is still an error.
    /// </param>
    public UpdateChecker(HttpMessageHandler? handler, EnvironmentIdentity? refusedHosts, string? ownFeed = null)
    {
        _handler = handler;
        _refused = refusedHosts;
        _ownFeed = string.IsNullOrWhiteSpace(ownFeed) ? null : ownFeed.Trim();
    }

    public async Task<UpdateCheckResult> CheckAsync(
        string? feedUrl,
        string currentInformationalVersion,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(feedUrl))
        {
            return new UpdateCheckResult
            {
                Kind = UpdateCheckKind.Unconfigured,
                Message = UnconfiguredMessage,
            };
        }

        var trimmed = feedUrl.Trim();
        if (IsNetworkPath(trimmed))
            return Error(NetworkShareMessage);

        try
        {
            string body;
            var local = TryResolveLocalFeedPath(trimmed, out var localPath);
            if (local)
            {
                if (!File.Exists(localPath))
                {
                    return Error("Could not check for updates: feed file was not found.");
                }

                body = await File.ReadAllTextAsync(localPath, cancellationToken).ConfigureAwait(false);
            }
            else if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            {
                if (_refused?.NamesHost(uri.IdnHost) == true)
                    return Error(ProductionFeedMessage);
                using var client = CreateClient();
                using var response = await client.GetAsync(uri, cancellationToken).ConfigureAwait(false);
                if (response.RequestMessage?.RequestUri is { } final && final.Scheme != Uri.UriSchemeHttps)
                {
                    return Error(LeftHttpsMessage);
                }

                if (response.RequestMessage?.RequestUri is { } landed && _refused?.NamesHost(landed.IdnHost) == true)
                    return Error(ProductionFeedMessage);

                body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound
                    && _ownFeed is not null
                    && string.Equals(trimmed, _ownFeed, StringComparison.OrdinalIgnoreCase))
                {
                    return NoReleaseYet(currentInformationalVersion);
                }

                if (!response.IsSuccessStatusCode)
                {
                    return Error($"Could not check for updates: HTTP {(int)response.StatusCode}.");
                }
            }
            else if (Uri.TryCreate(trimmed, UriKind.Absolute, out var http) && http.Scheme == Uri.UriSchemeHttp)
            {
                return Error(HttpsOnlyMessage);
            }
            else
            {
                return Error("Could not check for updates: the feed must be an https address or a local JSON file.");
            }

            return EvaluateBody(body, currentInformationalVersion, local);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Error(UnreachableMessage);
        }
        catch (HttpRequestException)
        {
            return Error(UnreachableMessage);
        }
        catch (Exception ex)
        {
            return Error("Could not check for updates: " + ex.Message);
        }
    }

    private static UpdateCheckResult EvaluateBody(string body, string currentInformationalVersion, bool feedIsLocal)
    {
        FeedPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<FeedPayload>(body, JsonOptions);
        }
        catch (JsonException ex)
        {
            return Error("Could not check for updates: feed was not JSON (" + ex.Message + ").");
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.Version))
        {
            return Error("Could not check for updates: feed JSON is missing a version.");
        }

        var feedVersion = payload.Version.Trim();
        var appInstallerUrl = string.IsNullOrWhiteSpace(payload.AppInstallerUrl)
            ? null
            : payload.AppInstallerUrl.Trim();
        var current = string.IsNullOrWhiteSpace(currentInformationalVersion)
            ? "0.0.0"
            : currentInformationalVersion.Trim();

        if (!TryParseNumericVersion(feedVersion, out var feedVer))
        {
            return new UpdateCheckResult
            {
                Kind = UpdateCheckKind.Error,
                FeedVersion = feedVersion,
                AppInstallerUrl = appInstallerUrl,
                FeedIsLocal = feedIsLocal,
                Message = "Could not check for updates: feed version is not comparable.",
            };
        }

        TryParseNumericVersion(current, out var currentVer);
        if (Canonical(feedVer) > Canonical(currentVer))
        {
            var urlLine = appInstallerUrl is null ? "" : "\n" + appInstallerUrl;
            return new UpdateCheckResult
            {
                Kind = UpdateCheckKind.UpdateAvailable,
                FeedVersion = feedVersion,
                AppInstallerUrl = appInstallerUrl,
                FeedIsLocal = feedIsLocal,
                Message = "Update available: " + feedVersion + urlLine,
            };
        }

        return new UpdateCheckResult
        {
            Kind = UpdateCheckKind.UpToDate,
            FeedVersion = feedVersion,
            AppInstallerUrl = appInstallerUrl,
            FeedIsLocal = feedIsLocal,
            Message = "You're up to date (" + current + ").",
        };
    }

    /// <summary>
    /// A UNC or device path, however it is spelled. Opening one makes Windows sign in to whoever answers, which hands
    /// them the user's credentials to crack or relay, so no update path may name one. The string, a file URI's decoded
    /// path, and the full path Windows would open are all checked: <c>file:///%2F%2Fhost/share</c> decodes to one.
    /// </summary>
    internal static bool IsNetworkPath(string trimmed)
    {
        if (LooksLikeNetworkPath(trimmed))
            return true;
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.IsFile)
            return uri.IsUnc || LooksLikeNetworkPath(uri.LocalPath) || LooksLikeNetworkPath(FullPath(uri.LocalPath));
        return Path.IsPathRooted(trimmed) && LooksLikeNetworkPath(FullPath(trimmed));
    }

    private static bool LooksLikeNetworkPath(string path)
        => path.Length >= 2 && (path[0] is '\\' or '/') && (path[1] is '\\' or '/');

    private static string FullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "";
        }
    }

    internal static bool TryResolveLocalFeedPath(string trimmed, out string path)
    {
        path = "";
        if (string.IsNullOrWhiteSpace(trimmed) || IsNetworkPath(trimmed))
            return false;

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                return false;
            if (uri.IsFile)
            {
                // LocalPath is already decoded. Decoding it again turned %252F into a second slash (WP-356 review)
                // and renamed a file called a%20b.json to a b.json.
                path = uri.LocalPath;
                return !string.IsNullOrWhiteSpace(path);
            }
        }

        if (!Path.IsPathRooted(trimmed))
            return false;

        path = trimmed;
        return true;
    }

    private static UpdateCheckResult Error(string message)
        => new()
        {
            Kind = UpdateCheckKind.Error,
            Message = message,
        };

    /// <summary>The build's own feed has no release yet, so nothing is newer than what is installed.</summary>
    private static UpdateCheckResult NoReleaseYet(string currentInformationalVersion)
        => new()
        {
            Kind = UpdateCheckKind.UpToDate,
            Message = "You're up to date ("
                + (string.IsNullOrWhiteSpace(currentInformationalVersion) ? "0.0.0" : currentInformationalVersion.Trim()) + ").",
        };

    private HttpClient CreateClient()
    {
        var client = _handler is null
            ? new HttpClient()
            : new HttpClient(_handler, disposeHandler: false);
        client.Timeout = TimeSpan.FromSeconds(15);
        client.MaxResponseContentBufferSize = MaxFeedBytes;
        return client;
    }

    public static bool TryParseNumericVersion(string value, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var s = value.Trim();
        if (s.StartsWith('v') || s.StartsWith('V'))
            s = s[1..].Trim();
        var cut = s.IndexOfAny(['-', '+']);
        if (cut >= 0)
            s = s[..cut];
        if (!s.Contains('.', StringComparison.Ordinal))
            s += ".0";

        if (!Version.TryParse(s, out var parsed) || parsed is null)
            return false;

        version = parsed;
        return true;
    }

    internal static Version Canonical(Version v)
        => new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

    private sealed class FeedPayload
    {
        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("appInstallerUrl")]
        public string? AppInstallerUrl { get; set; }
    }
}
