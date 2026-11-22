using System.Net.Http;
using System.Reflection;
using Temno.Core.IO;
using Temno.Environments;

namespace Temno.Core.Account;

/// <summary>
/// Public configuration for native hosted account/generation. Tokens never live here.
/// The anon key is publishable; it is not a Google, service-role or OpenRouter secret.
/// These are product infrastructure, never user settings: a user-editable gateway or redirect
/// would send the signed-in JWT wherever the box pointed, so <c>user-preferences.json</c> is not
/// consulted. A production build uses <see cref="EnvironmentIdentity.Production"/> only; a
/// development build uses <c>TEMNO_*</c> variables, then <see cref="DevelopmentBackendFile"/>,
/// then nothing, and never a production value (docs/contracts/environments.md).
/// </summary>
public static class HostedEndpoints
{
    public const string DefaultRedirectUri = "http://127.0.0.1:17321/callback";
    public const int CallbackPort = 17321;
    public const string CallbackPath = "/callback";
    public const string DefaultScopes = "email profile";

    public const string BuildEnvironmentMetadataKey = "TemnoEnvironment";

    public const string ProductionNotConfiguredMessage =
        "This build has no production backend yet. Sign-in and hosted features are off.";

    public const string DevelopmentNotConfiguredMessage =
        "Development backend not configured. Add " + DevelopmentBackendFile.DisplayPath + " or set the TEMNO_* variables.";

    /// <summary>The environment compiled into Temno.Core by the MSBuild property <c>TemnoEnvironment</c>.</summary>
    public static TemnoEnvironment BuildEnvironment { get; } = ReadBuildEnvironment(typeof(HostedEndpoints).Assembly);

    /// <summary>Production only for the exact metadata value <c>production</c>; anything else is development.</summary>
    public static TemnoEnvironment ReadBuildEnvironment(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        foreach (var attribute in assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (string.Equals(attribute.Key, BuildEnvironmentMetadataKey, StringComparison.Ordinal))
                return string.Equals(attribute.Value, TemnoEnvironments.ProductionName, StringComparison.Ordinal)
                    ? TemnoEnvironment.Production
                    : TemnoEnvironment.Development;
        }

        return TemnoEnvironment.Development;
    }

    /// <summary>
    /// This build's backend, resolved on every read so a changed variable or developer file is
    /// seen without a restart.
    /// </summary>
    public static HostedBackend Current
        => Resolve(BuildEnvironment, EnvironmentIdentity.Production, EnvCompat.Get, DevelopmentBackendFile.ReadText);

    public static string ResolveSupabaseUrl() => Current.SupabaseUrl;

    /// <summary>The publishable project key; server credentials never belong in the client.</summary>
    public static string ResolveAnonKey() => Current.AnonKey;

    /// <summary>The trusted hosted gateway.</summary>
    public static string ResolveGatewayUrl() => Current.GatewayUrl;

    /// <summary>The accounts (Billing) host: checkout, portal and plans (WP-262).</summary>
    public static string ResolveAccountsUrl() => Current.AccountsUrl;

    public static string ResolveRedirectUri() => Current.RedirectUri;

    public static bool GatewayConfigured() => Current.GatewayConfigured;

    public static bool AuthConfigured() => Current.AuthConfigured;

    public static bool AccountsConfigured() => Current.AccountsConfigured;

    /// <summary>
    /// The update feed this build reads when no setting names one (WP-375): production's compiled
    /// <see cref="EnvironmentIdentity.UpdateFeedUrl"/>, or development's <c>updateFeedUrl</c> in the developer file.
    /// </summary>
    public static string ResolveUpdateFeedUrl() => Current.UpdateFeedUrl;

    /// <summary>Why hosted features are off or incomplete, in words a user can act on.</summary>
    public static string NotConfiguredMessage
        => Current.Problem ?? "Account sign-in is not configured in this build.";

    /// <summary>
    /// Marks a request to the Gateway or Billing with this build's environment. Supabase requests
    /// never carry it. Production services refuse any other value (docs/contracts/environments.md).
    /// </summary>
    public static void StampEnvironment(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Headers.Remove(TemnoEnvironments.HeaderName);
        request.Headers.TryAddWithoutValidation(TemnoEnvironments.HeaderName, BuildEnvironment.Name());
    }

    /// <summary>
    /// The backend for <paramref name="environment"/>. A production build reads
    /// <paramref name="production"/> only and never calls <paramref name="env"/> or
    /// <paramref name="readDeveloperFile"/>. A development build takes each value from
    /// <paramref name="env"/>, then the developer file, and refuses the whole backend when any
    /// value belongs to <paramref name="production"/>.
    /// </summary>
    public static HostedBackend Resolve(
        TemnoEnvironment environment,
        EnvironmentIdentity production,
        Func<string, string?> env,
        Func<string?> readDeveloperFile)
    {
        ArgumentNullException.ThrowIfNull(production);
        ArgumentNullException.ThrowIfNull(env);
        ArgumentNullException.ThrowIfNull(readDeveloperFile);
        return environment == TemnoEnvironment.Production
            ? ResolveProduction(production)
            : ResolveDevelopment(production, env, readDeveloperFile);
    }

    private static HostedBackend ResolveProduction(EnvironmentIdentity production)
    {
        var supabaseUrl = production.SupabaseUrl;
        var anonKey = production.SupabaseAnonKey.Trim();
        var gatewayUrl = production.GatewayUrl;
        var accountsUrl = production.AccountsUrl;
        if (supabaseUrl.Length == 0 && anonKey.Length == 0 && gatewayUrl.Length == 0 && accountsUrl.Length == 0)
            return HostedBackend.Off(TemnoEnvironment.Production, ProductionNotConfiguredMessage)
                with { UpdateFeedUrl = production.UpdateFeedUrl };

        var missing = Missing(supabaseUrl, anonKey, gatewayUrl, accountsUrl, "project", "publishable key", "gateway host", "accounts host");
        var problem = missing is null
            ? null
            : $"This build's production backend has no {missing}. Hosted features that need it are off.";
        return new HostedBackend(TemnoEnvironment.Production, supabaseUrl, anonKey, gatewayUrl, accountsUrl,
            DefaultRedirectUri, problem, production.EventsUrl, production.UpdateFeedUrl);
    }

    private static HostedBackend ResolveDevelopment(
        EnvironmentIdentity production,
        Func<string, string?> env,
        Func<string?> readDeveloperFile)
    {
        var file = new DevelopmentBackendValues(null, null, null, null);
        string? json;
        try
        {
            json = readDeveloperFile();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return HostedBackend.Off(TemnoEnvironment.Development, $"{DevelopmentBackendFile.DisplayPath} could not be read. Hosted features are off.");
        }

        if (json is not null)
        {
            if (!DevelopmentBackendFile.TryParse(json, out file, out var fileProblem))
                return HostedBackend.Off(TemnoEnvironment.Development, fileProblem!);
        }

        var supabase = Pick(env, "TEMNO_SUPABASE_URL", file.SupabaseUrl, "supabaseUrl");
        var anon = Pick(env, "TEMNO_SUPABASE_ANON_KEY", file.SupabaseAnonKey, "supabaseAnonKey");
        var gateway = Pick(env, "TEMNO_GATEWAY_URL", file.GatewayUrl, "gatewayUrl");
        var accounts = Pick(env, "TEMNO_ACCOUNTS_URL", file.AccountsUrl, "accountsUrl");
        // Optional (WP-350): without it the app finds changes by polling, as before.
        var events = Pick(env, "TEMNO_EVENTS_URL", file.EventsUrl, "eventsUrl");
        var redirect = FirstNonEmpty(env("TEMNO_AUTH_REDIRECT_URI"), DefaultRedirectUri);
        // WP-375: only the file names the build's feed. TEMNO_UPDATE_FEED_URL already overrides the Settings field,
        // which overrides this (UserPreferences.ResolveUpdateFeedUrl). The feed needs none of the other values.
        var update = new Sourced(file.UpdateFeedUrl?.Trim() ?? "", $"\"updateFeedUrl\" in {DevelopmentBackendFile.DisplayPath}");
        if (UpdateFeedProblem(update, production) is { } updateProblem)
            return HostedBackend.Off(TemnoEnvironment.Development, updateProblem, redirect);

        if (supabase.Value.Length == 0 && anon.Value.Length == 0 && gateway.Value.Length == 0 && accounts.Value.Length == 0)
            return HostedBackend.Off(TemnoEnvironment.Development, DevelopmentNotConfiguredMessage, redirect)
                with { UpdateFeedUrl = update.Value };

        foreach (var url in new[] { supabase, gateway, accounts, events })
        {
            if (url.Value.Length == 0)
                continue;
            if (!IsServiceUrl(url.Value))
                return HostedBackend.Off(
                    TemnoEnvironment.Development,
                    $"{url.Source} must be an absolute https URL (http only on this computer). Hosted features are off.",
                    redirect);
            if (production.NamesHostOf(url.Value) || production.NamesSupabaseProject(url.Value))
                return HostedBackend.Off(
                    TemnoEnvironment.Development,
                    $"{url.Source} points at production. A development build never uses production, so hosted features are off.",
                    redirect);
        }

        if (anon.Value.Length > 0 && production.SupabaseAnonKey.Length > 0
            && string.Equals(anon.Value, production.SupabaseAnonKey.Trim(), StringComparison.Ordinal))
            return HostedBackend.Off(
                TemnoEnvironment.Development,
                $"{anon.Source} is production's publishable key. A development build never uses production, so hosted features are off.",
                redirect);

        var missing = Missing(supabase.Value, anon.Value, gateway.Value, accounts.Value, "supabaseUrl", "supabaseAnonKey", "gatewayUrl", "accountsUrl");
        var problem = missing is null
            ? null
            : $"Development backend is missing {missing}. Add it to {DevelopmentBackendFile.DisplayPath} or set the TEMNO_* variables.";
        return new HostedBackend(
            TemnoEnvironment.Development,
            supabase.Value.TrimEnd('/'),
            anon.Value,
            gateway.Value.TrimEnd('/'),
            accounts.Value.TrimEnd('/'),
            redirect,
            problem,
            events.Value.TrimEnd('/'),
            update.Value);
    }

    /// <summary>
    /// A feed is https, as the update check requires (WP-356), carries no credentials, and is never on a production
    /// host. The same refusal as the other values: hosted features go off with the reason.
    /// </summary>
    private static string? UpdateFeedProblem(Sourced feed, EnvironmentIdentity production)
    {
        if (feed.Value.Length == 0)
            return null;
        if (!Uri.TryCreate(feed.Value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0)
            return $"{feed.Source} must be an absolute https URL. Hosted features are off.";
        if (production.NamesHostOf(feed.Value))
            return $"{feed.Source} points at production. A development build never uses production, so hosted features are off.";
        return null;
    }

    /// <summary>Absolute https, or http on a loopback host, with no credentials, query or fragment.</summary>
    public static bool IsServiceUrl(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)
           && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0;

    public static bool TryParseLoopbackRedirect(string redirectUri, out int port, out string path)
    {
        port = CallbackPort;
        path = CallbackPath;
        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri))
            return false;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
            return false;
        port = uri.IsDefaultPort ? 80 : uri.Port;
        path = string.IsNullOrEmpty(uri.AbsolutePath) ? CallbackPath : uri.AbsolutePath;
        return true;
    }

    private readonly record struct Sourced(string Value, string Source);

    private static Sourced Pick(Func<string, string?> env, string variable, string? fileValue, string member)
    {
        var fromEnv = env(variable);
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return new Sourced(fromEnv.Trim(), variable);
        if (!string.IsNullOrWhiteSpace(fileValue))
            return new Sourced(fileValue.Trim(), $"\"{member}\" in {DevelopmentBackendFile.DisplayPath}");
        return new Sourced("", variable);
    }

    private static string? Missing(string supabase, string anon, string gateway, string accounts,
        string supabaseName, string anonName, string gatewayName, string accountsName)
    {
        var names = new List<string>(4);
        if (supabase.Length == 0) names.Add(supabaseName);
        if (anon.Length == 0) names.Add(anonName);
        if (gateway.Length == 0) names.Add(gatewayName);
        if (accounts.Length == 0) names.Add(accountsName);
        return names.Count == 0 ? null : string.Join(", ", names);
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return "";
    }
}

/// <summary>
/// One build's resolved backend. Each feature is on when its values are present and nothing was
/// refused; <see cref="Problem"/> is null only when every value is present.
/// </summary>
public sealed record HostedBackend(
    TemnoEnvironment Environment,
    string SupabaseUrl,
    string AnonKey,
    string GatewayUrl,
    string AccountsUrl,
    string RedirectUri,
    string? Problem,
    string EventsUrl = "",
    string UpdateFeedUrl = "")
{
    public bool AuthConfigured => SupabaseUrl.Length > 0 && AnonKey.Length > 0;

    public bool GatewayConfigured => GatewayUrl.Length > 0;

    public bool AccountsConfigured => AccountsUrl.Length > 0;

    /// <summary>The account events Worker (WP-350, docs/contracts/account-events.md). Optional.</summary>
    public bool EventsConfigured => EventsUrl.Length > 0;

    internal static HostedBackend Off(TemnoEnvironment environment, string problem, string redirectUri = HostedEndpoints.DefaultRedirectUri)
        => new(environment, "", "", "", "", redirectUri, problem);
}
