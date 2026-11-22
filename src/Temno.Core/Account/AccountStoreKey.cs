using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Temno.Environments;

namespace Temno.Core.Account;

/// <summary>
/// Where a signed-in session is stored: one directory per Supabase project, so a token issued by
/// one project is never offered to another (WP-260, docs/contracts/environments.md).
/// </summary>
public static class AccountStoreKey
{
    public const string SessionFileName = "session.dpapi";

    /// <summary>DPAPI entropy of the pre-WP-260 store at <c>account\session.dpapi</c>.</summary>
    public const string LegacyEntropy = "Temno.Account.v1";

    public const string EntropyPrefix = "Temno.Account.v2|";

    /// <summary>
    /// The project's origin, <c>scheme://host[:port]</c> in lowercase with the default port
    /// omitted, or null when <paramref name="supabaseUrl"/> is not an absolute http(s) URL. A DNS
    /// host is read the way <see cref="EnvironmentIdentity"/> compares hosts
    /// (<see cref="EnvironmentIdentity.BareHost"/> of <see cref="Uri.IdnHost"/>), so a trailing
    /// dot, an uppercase or a full-width spelling names the same project. An IPv6 host keeps its
    /// brackets. The origin of an already canonical URL is the URL itself, so the keys of stored
    /// sessions do not move.
    /// </summary>
    public static string? Origin(string? supabaseUrl)
    {
        if (!Uri.TryCreate(supabaseUrl?.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return null;
        var host = uri.HostNameType == UriHostNameType.IPv6 ? uri.Host : EnvironmentIdentity.BareHost(uri.IdnHost);
        if (host.Length == 0)
            return null;
        var port = uri.IsDefaultPort ? "" : ":" + uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return (uri.Scheme + "://" + host + port).ToLowerInvariant();
    }

    /// <summary>
    /// True when a session taken while <paramref name="boundOrigin"/> was the project may still be
    /// used now that <paramref name="currentSupabaseUrl"/> is: both name the same project. False
    /// when either is missing (sign-in is off, or the developer file cannot be read) or they name
    /// different projects. The desktop drops the session from memory when this turns false and
    /// sends none of its tokens anywhere; neither project's stored session is touched.
    /// </summary>
    public static bool SameProject(string? boundOrigin, string? currentSupabaseUrl)
    {
        var bound = Origin(boundOrigin);
        return bound is not null && string.Equals(bound, Origin(currentSupabaseUrl), StringComparison.Ordinal);
    }

    /// <summary>The first 16 lowercase hex characters of SHA-256 of <paramref name="origin"/>.</summary>
    public static string Key(string origin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(origin));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    public static byte[] Entropy(string origin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);
        return Encoding.UTF8.GetBytes(EntropyPrefix + origin);
    }

    public static string SessionPath(string accountRoot, string origin)
        => Path.Combine(accountRoot, Key(origin), SessionFileName);

    public static string LegacySessionPath(string accountRoot)
        => Path.Combine(accountRoot, SessionFileName);

    /// <summary>
    /// The unverified <c>iss</c> claim of a JWT, or null. The signature is not checked: this only
    /// decides which project's store a token we already hold belongs to.
    /// </summary>
    public static string? Issuer(string? jwt)
    {
        if (string.IsNullOrWhiteSpace(jwt))
            return null;
        var parts = jwt.Split('.');
        if (parts.Length != 3 || parts[1].Length == 0)
            return null;
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("iss", out var iss)
                   && iss.ValueKind == JsonValueKind.String
                ? iss.GetString()
                : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }

    /// <summary>True when <paramref name="jwt"/> was issued by the Supabase Auth of <paramref name="origin"/>, <c>&lt;origin&gt;/auth/v1</c>.</summary>
    public static bool IssuedBy(string? jwt, string origin)
    {
        if (string.IsNullOrWhiteSpace(origin)
            || !Uri.TryCreate(Issuer(jwt), UriKind.Absolute, out var issuer)
            || issuer.Query.Length != 0 || issuer.Fragment.Length != 0 || issuer.UserInfo.Length != 0)
            return false;
        return string.Equals(Origin(issuer.AbsoluteUri), origin, StringComparison.Ordinal)
               && string.Equals(issuer.AbsolutePath.TrimEnd('/'), "/auth/v1", StringComparison.Ordinal);
    }
}

/// <summary>Protects a stored session at rest. The desktop uses DPAPI for the current user.</summary>
public interface ISessionProtector
{
    byte[] Protect(byte[] plain, byte[] entropy);

    /// <summary>Throws <see cref="CryptographicException"/> when the bytes were not protected with this entropy.</summary>
    byte[] Unprotect(byte[] protectedBytes, byte[] entropy);
}

/// <summary>
/// A session store keyed by the current Supabase project (<see cref="AccountStoreKey"/>). With no
/// project configured it holds nothing. It writes and returns only a session whose access token
/// the current project issued, so another project's token is never stored or loaded under this
/// project's key. A pre-WP-260 session moves into the keyed store only when its access token was
/// issued by the current project; otherwise it is left untouched.
/// </summary>
public sealed class ProjectSessionStore : IAccountTokenStore
{
    private static readonly byte[] LegacyEntropyBytes = Encoding.UTF8.GetBytes(AccountStoreKey.LegacyEntropy);
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _root;
    private readonly Func<string?> _projectUrl;
    private readonly ISessionProtector _protector;

    /// <param name="accountRoot">The <c>account</c> directory.</param>
    /// <param name="projectUrl">The current Supabase URL, or null when sign-in is not configured.</param>
    public ProjectSessionStore(string accountRoot, Func<string?> projectUrl, ISessionProtector protector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountRoot);
        _root = accountRoot;
        _projectUrl = projectUrl ?? throw new ArgumentNullException(nameof(projectUrl));
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
    }

    public void Save(AccountTokenBundle bundle) => TrySave(bundle);

    /// <summary>
    /// Writes <paramref name="bundle"/> under the current project's key and returns true. Writes
    /// nothing and returns false when no project is configured or the current project did not
    /// issue the access token (the project changed after the token was taken).
    /// </summary>
    public bool TrySave(AccountTokenBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        if (CurrentOrigin() is not { } origin || !AccountStoreKey.IssuedBy(bundle.AccessToken, origin))
            return false;
        Write(AccountStoreKey.SessionPath(_root, origin), origin, bundle);
        return true;
    }

    /// <summary>
    /// The current project's session, or null. A keyed file holding an access token that another
    /// project issued is not returned, and it is left in place.
    /// </summary>
    public AccountTokenBundle? Load()
    {
        if (CurrentOrigin() is not { } origin)
            return null;
        var path = AccountStoreKey.SessionPath(_root, origin);
        if (File.Exists(path))
        {
            AccountTokenBundle? bundle;
            try
            {
                bundle = Read(path, AccountStoreKey.Entropy(origin));
            }
            catch (Exception ex) when (ex is CryptographicException or JsonException or IOException)
            {
                try { File.Delete(path); } catch { /* ignore */ }
                return null;
            }

            return bundle is not null && AccountStoreKey.IssuedBy(bundle.AccessToken, origin) ? bundle : null;
        }

        return MigrateLegacy(path, origin);
    }

    /// <summary>
    /// Deletes the current project's session only. The desktop drops a session from memory when
    /// the project changes, so it never signs one project out while another is current.
    /// </summary>
    public void Clear()
    {
        if (CurrentOrigin() is { } origin)
            TryDelete(AccountStoreKey.SessionPath(_root, origin));
    }

    private AccountTokenBundle? MigrateLegacy(string path, string origin)
    {
        var legacy = AccountStoreKey.LegacySessionPath(_root);
        if (!File.Exists(legacy))
            return null;
        AccountTokenBundle? bundle;
        try
        {
            bundle = Read(legacy, LegacyEntropyBytes);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException)
        {
            return null;
        }

        if (bundle is null || !AccountStoreKey.IssuedBy(bundle.AccessToken, origin))
            return null;
        try
        {
            Write(path, origin, bundle);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            return bundle;
        }

        TryDelete(legacy);
        return bundle;
    }

    private AccountTokenBundle? Read(string path, byte[] entropy)
    {
        var json = Encoding.UTF8.GetString(_protector.Unprotect(File.ReadAllBytes(path), entropy));
        return JsonSerializer.Deserialize<AccountTokenBundle>(json, Json);
    }

    private void Write(string path, string origin, AccountTokenBundle bundle)
    {
        var protectedBytes = _protector.Protect(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(bundle, Json)),
            AccountStoreKey.Entropy(origin));
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, protectedBytes);
        File.Move(tmp, path, overwrite: true);
    }

    private string? CurrentOrigin() => AccountStoreKey.Origin(_projectUrl());

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* ignore */ }
    }
}
