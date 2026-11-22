using System.Text.Json;

namespace Temno.Environments;

/// <summary>
/// The two backends a Temno build or service can belong to. There is no third value and no
/// default: <see cref="TemnoEnvironments.TryParse"/> accepts only the exact lowercase names.
/// </summary>
public enum TemnoEnvironment
{
    Development,
    Production,
}

/// <summary>
/// Names shared by the desktop, the Gateway and Billing (docs/contracts/environments.md).
/// This file is compiled into Temno.Core and linked into both services, so it depends on the
/// base class library only.
/// </summary>
public static class TemnoEnvironments
{
    public const string DevelopmentName = "development";
    public const string ProductionName = "production";

    /// <summary>Configuration key both services require. It has no default.</summary>
    public const string ConfigurationKey = "Temno:Environment";

    /// <summary>Header every desktop request to the Gateway or Billing carries.</summary>
    public const string HeaderName = "X-Temno-Environment";

    public static bool TryParse(string? value, out TemnoEnvironment environment)
    {
        switch (value)
        {
            case DevelopmentName:
                environment = TemnoEnvironment.Development;
                return true;
            case ProductionName:
                environment = TemnoEnvironment.Production;
                return true;
            default:
                environment = TemnoEnvironment.Development;
                return false;
        }
    }

    public static string Name(this TemnoEnvironment environment) => environment switch
    {
        TemnoEnvironment.Production => ProductionName,
        _ => DevelopmentName,
    };

    /// <summary>
    /// Whether a service in <paramref name="service"/> answers a request whose header says
    /// <paramref name="header"/>. Production accepts only production. Development refuses only a
    /// build that names another environment, so scripts and receipts without the header still
    /// reach it. This prevents accidents; it is not a security control.
    /// </summary>
    public static bool Accepts(this TemnoEnvironment service, string? header)
    {
        if (string.IsNullOrEmpty(header))
            return service == TemnoEnvironment.Development;
        return TryParse(header, out var build) && build == service;
    }
}

/// <summary>
/// One environment's public identifiers: never a secret. <see cref="Production"/> is the only
/// place production's identity lives in source. Development's is deliberately absent: it
/// comes from a developer file or <c>TEMNO_*</c> variables on the desktop and from host
/// configuration on the services, so the public repository never ships it.
/// </summary>
public sealed record EnvironmentIdentity(
    string SupabaseProjectRef,
    string SupabaseAnonKey,
    string GatewayHost,
    string AccountsHost,
    string StripeAccountId,
    string EmailSender,
    string SupabaseCustomHost = "",
    string EventsHost = "",
    string UpdateHost = "")
{
    /// <summary>Where the update feed lives on <see cref="UpdateHost"/> (WP-375, docs/MSIX_RELEASE.md).</summary>
    public const string UpdateFeedPath = "/windows/stable.json";

    /// <summary>
    /// Production's public identity. The live Stripe account is Temno's (WP-263). The custom
    /// domain is an alias for boundary checks; the observed token issuer retains the project origin.
    /// The update host is production's release bucket (WP-376), so an installed Temno finds updates with no setting.
    /// </summary>
    public static EnvironmentIdentity Production { get; } = new(
        SupabaseProjectRef: "fshqvdygdtbkrrbtgpda",
        SupabaseAnonKey: "sb_publishable_3TxCcK_WV0me7c27He4N5w_7tE33Oom",
        GatewayHost: "gateway.temno.io",
        AccountsHost: "accounts.temno.io",
        StripeAccountId: "acct_1UEHttRzwSqmSvUB",
        EmailSender: "noreply@auth.temno.io",
        SupabaseCustomHost: "auth.temno.io",
        EventsHost: "gateway.temno.io",
        UpdateHost: "download.temno.io");

    public string SupabaseUrl => SupabaseProjectRef.Length == 0 ? "" : $"https://{SupabaseProjectRef}.supabase.co";

    public string GatewayUrl => GatewayHost.Length == 0 ? "" : $"https://{GatewayHost}";

    public string AccountsUrl => AccountsHost.Length == 0 ? "" : $"https://{AccountsHost}";

    public string EventsUrl => EventsHost.Length == 0 ? "" : $"https://{EventsHost}";

    /// <summary>The update feed this identity's builds read when no setting names one.</summary>
    public string UpdateFeedUrl => UpdateHost.Length == 0 ? "" : $"https://{UpdateHost}{UpdateFeedPath}";

    private IEnumerable<string> Hosts
        => new[] { GatewayHost, AccountsHost, EventsHost, UpdateHost }.Where(h => h.Length > 0);

    /// <summary>True when <paramref name="host"/> is one of this identity's hosts.</summary>
    public bool NamesHost(string? host)
    {
        var bare = BareHost(host);
        return bare.Length > 0 && Hosts.Any(h => string.Equals(bare, h, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// True when a host-filter entry answers on one of this identity's hosts: the host itself, or a
    /// wildcard such as <c>*.temno.io</c> above it. A bare <c>*</c> is not counted; it is the
    /// framework's any-host default.
    /// </summary>
    public bool CoversHost(string? entry)
    {
        if (NamesHost(entry))
            return true;
        var bare = BareHost(entry);
        if (!bare.StartsWith("*.", StringComparison.Ordinal))
            return false;
        var suffix = bare[1..];
        return Hosts.Any(h => h.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True when <paramref name="url"/> is an absolute URL on one of this identity's hosts.</summary>
    public bool NamesHostOf(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && NamesHost(uri.IdnHost);

    /// <summary>True when <paramref name="projectRefOrUrl"/> is this identity's Supabase project.</summary>
    public bool NamesSupabaseProject(string? projectRefOrUrl)
    {
        if (SupabaseProjectRef.Length == 0 || string.IsNullOrWhiteSpace(projectRefOrUrl))
            return false;
        var value = projectRefOrUrl.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            var host = BareHost(uri.IdnHost);
            if (SupabaseCustomHost.Length > 0
                && string.Equals(host, BareHost(SupabaseCustomHost), StringComparison.OrdinalIgnoreCase))
                return true;
            value = host.EndsWith(".supabase.co", StringComparison.OrdinalIgnoreCase)
                ? host[..^".supabase.co".Length]
                : host;
        }
        return string.Equals(value, SupabaseProjectRef, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when this identity has a host, so a host filter that answers on every host
    /// (<c>*</c>, or none configured) would answer on it too.
    /// </summary>
    public bool HasHosts => Hosts.Any();

    /// <summary>True when a host-filter entry is the framework's any-host <c>*</c>.</summary>
    public static bool IsAnyHost(string? entry) => BareHost(entry) == "*";

    /// <summary>
    /// A host name as DNS resolves it: trimmed, without a <c>:port</c>, and without the root's
    /// trailing dot, so <c>example.supabase.co.:443</c> compares equal to
    /// <c>example.supabase.co</c>. Pass <see cref="Uri.IdnHost"/> for a URL, so a full-width or
    /// Unicode spelling compares in its ASCII form. A bracketed or bare IPv6 address keeps its
    /// colons.
    /// </summary>
    public static string BareHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return "";
        var bare = host.Trim();
        var colon = bare.IndexOf(':');
        if (colon > 0 && colon == bare.LastIndexOf(':') && colon < bare.Length - 1
            && bare[(colon + 1)..].All(char.IsAsciiDigit))
            bare = bare[..colon];
        return bare.TrimEnd('.');
    }

    /// <summary>True when <paramref name="sender"/> (a bare address or <c>Name &lt;address&gt;</c>) is this identity's sender.</summary>
    public bool NamesEmailSender(string? sender)
        => EmailSender.Length > 0 && string.Equals(EmailAddressOf(sender), EmailSender, StringComparison.OrdinalIgnoreCase);

    public static string EmailAddressOf(string? sender)
    {
        if (string.IsNullOrWhiteSpace(sender))
            return "";
        var value = sender.Trim();
        var open = value.LastIndexOf('<');
        var close = value.LastIndexOf('>');
        return open >= 0 && close > open ? value[(open + 1)..close].Trim() : value;
    }
}

/// <summary>
/// The environment a service package was built for, written by the publish scripts as
/// <c>temno-environment.json</c> beside the service's DLL. A service refuses to start when the
/// stamp disagrees with <c>Temno:Environment</c>, and a production service refuses to start
/// without one, so a package uploaded to the wrong host fails before it serves anything.
/// </summary>
public static class ArtifactStamp
{
    public const string FileName = "temno-environment.json";

    public static string Serialize(TemnoEnvironment environment)
        => JsonSerializer.Serialize(new Dictionary<string, string> { ["environment"] = environment.Name() });

    public static bool TryParse(string? json, out TemnoEnvironment environment)
    {
        environment = TemnoEnvironment.Development;
        if (string.IsNullOrWhiteSpace(json))
            return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("environment", out var value)
                   && value.ValueKind == JsonValueKind.String
                   && TemnoEnvironments.TryParse(value.GetString(), out environment);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// The startup problem, or null. <paramref name="stampJson"/> is the file's text, or null
    /// when the file is absent (a source run or a test host).
    /// </summary>
    public static string? Problem(string? stampJson, TemnoEnvironment configured)
    {
        if (stampJson is null)
            return configured == TemnoEnvironment.Production
                ? $"This package carries no {FileName}. A production service runs only a package built with -Environment production."
                : null;
        if (!TryParse(stampJson, out var built))
            return $"{FileName} is unreadable; it must be {{\"environment\":\"development\"}} or {{\"environment\":\"production\"}}.";
        return built == configured
            ? null
            : $"This package was built for {built.Name()}, and {TemnoEnvironments.ConfigurationKey} is {configured.Name()}. Deploy the {configured.Name()} package here.";
    }
}
