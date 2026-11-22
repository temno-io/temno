using System.Text.Json.Serialization;

namespace Temno.Core.Account;

public sealed class AccountTokenBundle
{
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public string UserId { get; set; } = "";
    public string? Email { get; set; }
    public string? DisplayName { get; set; }
    public string? AvatarUrl { get; set; }
    public DateTimeOffset? AccessExpiresAt { get; set; }
}

public readonly record struct CreditExpiry(long Credits, DateTimeOffset At);

public readonly record struct CreditBalance(long Total, long Reserved, long Available)
{
    public bool OwnerTesting { get; init; }

    /// <summary>The spendable credits that expire soonest, and when; null when none expire.</summary>
    public CreditExpiry? NextExpiry { get; init; }

    public bool CanCover(long credits) => credits >= 0 && (OwnerTesting || Available >= credits);
}

public interface IAccountTokenStore
{
    void Save(AccountTokenBundle bundle);
    AccountTokenBundle? Load();
    void Clear();
}

public sealed class MemoryAccountTokenStore : IAccountTokenStore
{
    private AccountTokenBundle? _bundle;

    public void Save(AccountTokenBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        _bundle = bundle;
    }

    public AccountTokenBundle? Load() => _bundle;

    public void Clear() => _bundle = null;
}

public interface IAccountSessionSource
{
    bool IsSignedIn { get; }
    string? UserId { get; }
    string? Email { get; }
    string? AccessToken { get; }
    CreditBalance? Balance { get; }
    string? BalanceError { get; }
    event EventHandler? Changed;
    Task RefreshBalanceAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>Process-wide hosted session used by MCP generation routing. WinUI assigns this.</summary>
public static class HostedRuntime
{
    public static IAccountSessionSource? Session { get; set; }

    public static string? AccessToken => Session?.AccessToken;

    public static bool IsSignedIn => Session?.IsSignedIn == true;
}

public sealed class HostedApiException : InvalidOperationException
{
    public HostedApiException(string message, int? statusCode = null, string? code = null)
        : base(message)
    {
        StatusCode = statusCode;
        Code = code;
    }

    public int? StatusCode { get; }
    public string? Code { get; }
    // WP-254: set on hold_confirmation_required; resending with this explicit hold is the user's "yes".
    public long? SuggestedHoldCredits { get; init; }
    // WP-254 review: the user text of a submission that was definitely never admitted and has been taken back out
    // of the chat, so the caller can return it to the composer.
    public string? WithdrawnText { get; init; }

    public bool IsInsufficientCredits =>
        string.Equals(Code, "insufficient_credits", StringComparison.OrdinalIgnoreCase)
        || StatusCode == 402;

    public bool IsProviderDown =>
        StatusCode is 503
        || Code is "dependency_unavailable" or "not_configured" or "generation_disabled"
            or "transcription_not_ready" or "agent_not_ready" or "artifact_store_unavailable"
            or "openrouter_unfunded" or "transcription_provider_unfunded"
            or "transcription_funding_unavailable";

    public bool IsUnconfigured =>
        Code is "not_configured" or "generation_disabled" or "agent_not_ready" or "transcription_not_ready";
}

public sealed class SignInIntent
{
    public required string Verifier { get; init; }
    public required string Challenge { get; init; }
    public required string RedirectUri { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public bool Consumed { get; set; }
}

public enum SignInOutcomeKind
{
    Exchanged,
    Rejected,
    Expired,
    Replay,
    Cancelled,
    Unknown,
}

public readonly record struct SignInOutcome(SignInOutcomeKind Kind, AccountTokenBundle? Session = null, string? Detail = null);
