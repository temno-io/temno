namespace Temno.Core.Account;

public sealed class GoogleSignInFlow
{
    /// <summary>
    /// How long a sign-in may take, from opening the browser to Google's redirect. Covers the sign-in page's check and
    /// Google's account chooser; the page refuses its start request after the same window (Billing's LinkMinutes).
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    /// <summary>The sign-in page's path on the accounts host (WP-354, docs/contracts/google-sign-in.md § 8).</summary>
    public const string StartPath = "/auth/start";

    private readonly object _gate = new();
    private SignInIntent? _intent;

    /// <summary>
    /// The Temno sign-in page for this sign-in: it runs the Turnstile check, records a ticket for the challenge, then
    /// sends the browser to Supabase's Google authorize URL with the same challenge and loopback. Null when the accounts
    /// host is not a usable https (or local test) origin; the desktop then goes to Google directly and gets no grant.
    /// </summary>
    public static string? StartPageUrl(string accountsUrl, string challenge, string redirectUri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(challenge);
        ArgumentException.ThrowIfNullOrWhiteSpace(redirectUri);
        if (!Uri.TryCreate(accountsUrl, UriKind.Absolute, out var accounts)
            || !(accounts.Scheme == Uri.UriSchemeHttps || accounts.Scheme == Uri.UriSchemeHttp && accounts.IsLoopback)
            || accounts.UserInfo.Length != 0 || accounts.Query.Length != 0 || accounts.Fragment.Length != 0)
            return null;
        return accountsUrl.TrimEnd('/') + StartPath
            + "?code_challenge=" + Uri.EscapeDataString(challenge)
            + "&redirect_uri=" + Uri.EscapeDataString(redirectUri);
    }

    /// <summary>
    /// What the app says after a sign-in about the signup credits, from the bootstrap's signupGrant. Null when there is
    /// nothing to say: the account already had them, or the server is older than WP-354.
    /// </summary>
    public static string? GrantNote(string? signupGrant) => signupGrant switch
    {
        "applied" => "Your free credits are in your balance.",
        "no_ticket" => "This sign-in skipped Temno's sign-in check, so it added no free credits. Sign out and sign in again to claim them.",
        "ticket_used" or "ticket_expired" => "The sign-in check expired before Temno could use it, so no free credits were added. Sign out and sign in again to claim them.",
        "velocity_limited" => "Free credits have reached today's limit for this network, so none were added. Sign out and sign in again tomorrow to claim them.",
        "unconfirmed" => "Free credits wait until this account's email is confirmed.",
        _ => null,
    };

    public SignInIntent Begin(string redirectUri, TimeSpan lifetime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(redirectUri);
        if (lifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(lifetime));

        var verifier = Pkce.NewVerifier();
        var intent = new SignInIntent
        {
            Verifier = verifier,
            Challenge = Pkce.ChallengeS256(verifier),
            RedirectUri = redirectUri,
            ExpiresAt = DateTimeOffset.UtcNow.Add(lifetime),
        };
        lock (_gate)
            _intent = intent;
        return intent;
    }

    public SignInIntent? Current
    {
        get { lock (_gate) return _intent; }
    }

    public void Cancel()
    {
        lock (_gate)
        {
            if (_intent is not null)
                _intent.Consumed = true;
            _intent = null;
        }
    }

    public async Task<SignInOutcome> CompleteAsync(
        CallbackResult callback,
        SupabaseAuthClient auth,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auth);
        SignInIntent intent;
        lock (_gate)
        {
            if (_intent is null)
                return new SignInOutcome(SignInOutcomeKind.Replay, Detail: "no_intent");
            intent = _intent;
            if (intent.Consumed)
                return new SignInOutcome(SignInOutcomeKind.Replay, Detail: "replay");
            if (intent.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                _intent = null;
                return new SignInOutcome(SignInOutcomeKind.Expired, Detail: "expired");
            }

            intent.Consumed = true;
            _intent = null;
        }

        if (callback.Kind == CallbackKind.Cancelled)
            return new SignInOutcome(SignInOutcomeKind.Cancelled, Detail: "cancelled");
        if (callback.Kind == CallbackKind.Expired)
            return new SignInOutcome(SignInOutcomeKind.Expired, Detail: "expired");
        if (callback.Kind != CallbackKind.Code || string.IsNullOrWhiteSpace(callback.Code))
            return new SignInOutcome(SignInOutcomeKind.Rejected, Detail: callback.Detail ?? "missing_code");

        return await auth.ExchangeCodeAsync(callback.Code, intent.Verifier, cancellationToken).ConfigureAwait(false);
    }
}
