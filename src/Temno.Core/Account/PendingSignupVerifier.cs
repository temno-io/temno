using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Temno.Core.Account;

/// <summary>
/// WP-390: the PKCE verifier of a sign-in whose bootstrap the Gateway has not answered yet. It is kept on disk so that
/// closing Temno after a failed bootstrap cannot lose the sign-up grant (docs/contracts/hosted-credits.md § The signup
/// grant and the sign-in page). Supabase has already spent the code it unlocked; it can only redeem this user's ticket.
/// </summary>
public sealed record PendingSignupVerifier(string UserId, string Origin, string Verifier, DateTimeOffset SignedInAt);

public interface ISignupVerifierStore
{
    PendingSignupVerifier? Load();
    void Save(PendingSignupVerifier pending);
    void Clear();
}

/// <summary>When a pending verifier is sent, retried and given up.</summary>
public static class SignupVerifierRules
{
    /// <summary>The sign-in page's ticket lives 15 minutes (<c>signup_grant_ticket_issue</c>). Retries stop there.</summary>
    public static readonly TimeSpan TicketLife = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How long a later start still sends a pending verifier, once, so the user gets the server's answer
    /// (<c>ticket_expired</c> tells them to sign in again) instead of 0 credits and no note. Tickets are kept two days.
    /// </summary>
    public static readonly TimeSpan PendingLife = TimeSpan.FromDays(1);

    /// <summary>
    /// The development Gateway wakes in about 17 s (WP-383 step 5) and its container front holds the request until it is
    /// ready, so the bootstrap that carries a verifier outlasts a wake. Plain balance reads keep 15 s.
    /// </summary>
    public static readonly TimeSpan BootstrapTimeout = TimeSpan.FromSeconds(30);

    /// <summary>5, 10, 20 and 40 s, then every minute. The fronts' 503s ask for 5 s, so Retry-After adds nothing.</summary>
    public static TimeSpan RetryGap(int attempt) => attempt switch
    {
        <= 0 => TimeSpan.FromSeconds(5),
        1 => TimeSpan.FromSeconds(10),
        2 => TimeSpan.FromSeconds(20),
        3 => TimeSpan.FromSeconds(40),
        _ => TimeSpan.FromSeconds(60),
    };

    /// <summary>
    /// A failure a later call could turn into an answer: no response (a transport failure or timeout), 401 (the next
    /// read refreshes the token), 408, 429 or 5xx. Any other refusal (a malformed verifier, a suspended account, the
    /// wrong environment) ends the attempt; <c>account_bootstrap</c> spends no ticket on a refused call.
    /// </summary>
    public static bool MayAnswerLater(Exception ex) => ex switch
    {
        OperationCanceledException => true,
        HostedApiException { StatusCode: null or 401 or 408 or 429 or >= 500 } => true,
        _ => false,
    };

    /// <summary>The pending verifier belongs to this session: the same account and project, and less than a day old.</summary>
    public static bool Belongs(PendingSignupVerifier pending, string? userId, string? origin, DateTimeOffset now) =>
        string.Equals(pending.UserId, userId, StringComparison.Ordinal)
        && string.Equals(pending.Origin, origin, StringComparison.Ordinal)
        && now - pending.SignedInAt < PendingLife
        && pending.SignedInAt <= now + TimeSpan.FromMinutes(5);

    /// <summary>Whether a failed bootstrap is still worth retrying on its own: the ticket may still be live.</summary>
    public static bool Retrying(PendingSignupVerifier pending, DateTimeOffset now) => now - pending.SignedInAt <= TicketLife;
}

public sealed class MemorySignupVerifierStore : ISignupVerifierStore
{
    private PendingSignupVerifier? _pending;

    public PendingSignupVerifier? Load() => _pending;

    public void Save(PendingSignupVerifier pending)
    {
        ArgumentNullException.ThrowIfNull(pending);
        _pending = pending;
    }

    public void Clear() => _pending = null;
}

/// <summary>
/// The pending verifier beside the project's session (<c>account\&lt;key&gt;\signup-verifier.dpapi</c>), protected like
/// it with its own entropy, so neither file can be read as the other. Like <see cref="ProjectSessionStore"/> it holds
/// only the current project's verifier and never touches another project's. One process writes it (one instance per
/// environment), so the value is cached after the first read.
/// </summary>
public sealed class ProjectSignupVerifierStore : ISignupVerifierStore
{
    public const string FileName = "signup-verifier.dpapi";
    public const string EntropyPrefix = "Temno.SignupVerifier.v1|";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly object _gate = new();
    private readonly string _root;
    private readonly Func<string?> _projectUrl;
    private readonly ISessionProtector _protector;
    private string? _cachedOrigin;
    private PendingSignupVerifier? _cached;

    /// <param name="accountRoot">The <c>account</c> directory.</param>
    /// <param name="projectUrl">The current Supabase URL, or null when sign-in is not configured.</param>
    public ProjectSignupVerifierStore(string accountRoot, Func<string?> projectUrl, ISessionProtector protector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountRoot);
        _root = accountRoot;
        _projectUrl = projectUrl ?? throw new ArgumentNullException(nameof(projectUrl));
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
    }

    public static string PathFor(string accountRoot, string origin)
        => Path.Combine(accountRoot, AccountStoreKey.Key(origin), FileName);

    public static byte[] Entropy(string origin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);
        return Encoding.UTF8.GetBytes(EntropyPrefix + origin);
    }

    /// <summary>The current project's pending verifier, or null. An unreadable file is deleted.</summary>
    public PendingSignupVerifier? Load()
    {
        if (AccountStoreKey.Origin(_projectUrl()) is not { } origin)
            return null;
        lock (_gate)
        {
            if (_cachedOrigin == origin)
                return _cached;
            var path = PathFor(_root, origin);
            PendingSignupVerifier? pending = null;
            if (File.Exists(path))
            {
                try
                {
                    var json = Encoding.UTF8.GetString(_protector.Unprotect(File.ReadAllBytes(path), Entropy(origin)));
                    pending = JsonSerializer.Deserialize<PendingSignupVerifier>(json, Json);
                }
                catch (Exception ex) when (ex is CryptographicException or JsonException or IOException
                    or UnauthorizedAccessException or NotSupportedException)
                {
                    TryDelete(path);
                }
            }

            if (pending is not null && !string.Equals(pending.Origin, origin, StringComparison.Ordinal))
                pending = null;
            _cachedOrigin = origin;
            _cached = pending;
            return pending;
        }
    }

    /// <summary>
    /// Writes nothing when the verifier's project is not the current one. A write that fails keeps the verifier for this
    /// process only: sign-in must not fail over it.
    /// </summary>
    public void Save(PendingSignupVerifier pending)
    {
        ArgumentNullException.ThrowIfNull(pending);
        if (AccountStoreKey.Origin(_projectUrl()) is not { } origin || !string.Equals(pending.Origin, origin, StringComparison.Ordinal))
            return;
        var path = PathFor(_root, origin);
        lock (_gate)
        {
            _cachedOrigin = origin;
            _cached = pending;
            try
            {
                var protectedBytes = _protector.Protect(
                    Encoding.UTF8.GetBytes(JsonSerializer.Serialize(pending, Json)), Entropy(origin));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var tmp = path + ".tmp";
                File.WriteAllBytes(tmp, protectedBytes);
                File.Move(tmp, path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
            {
                // Kept in memory; a restart before the answer loses it, as before WP-390.
            }
        }
    }

    /// <summary>Deletes the current project's pending verifier only.</summary>
    public void Clear()
    {
        if (AccountStoreKey.Origin(_projectUrl()) is not { } origin)
            return;
        lock (_gate)
        {
            TryDelete(PathFor(_root, origin));
            _cachedOrigin = origin;
            _cached = null;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* ignore */ }
    }
}
