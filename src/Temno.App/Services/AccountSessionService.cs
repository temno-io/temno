using System.Diagnostics;
using Temno.Core.Account;
using Temno.Core.Generation;

namespace Temno.App.Services;

/// <summary>
/// The signed-in session. It belongs to the Supabase project that was current when it was
/// applied (restored, signed in or refreshed). A development build re-reads its backend on every
/// use, so before a refresh and before any access token leaves the process the session is checked
/// against the project configured now; when the project changed the session is dropped from
/// memory, the app shows signed out, and neither project's stored session is touched (WP-260,
/// docs/contracts/environments.md: a token never crosses projects).
/// </summary>
public sealed class AccountSessionService : IAccountSessionSource
{
    public static AccountSessionService Current { get; } = new();

    private const string ProjectChangedDuringSignInMessage =
        "The development backend changed during sign-in, so the session was not kept. Sign in again.";

    private readonly object _gate = new();
    private readonly IAccountTokenStore _store;
    private readonly GoogleSignInFlow _flow = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private Microsoft.UI.Dispatching.DispatcherQueue? _dispatcher;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _refreshTimer;
    private AccountTokenBundle? _bundle;
    // The origin of the project _bundle belongs to (AccountStoreKey.Origin), set with _bundle.
    private string? _bundleOrigin;
    private CreditBalance? _balance;
    private string? _balanceError;
    private CancellationTokenSource? _signInCts;
    private LoopbackCallbackServer? _loopback;
    // WP-350: the account's push channel, one per signed-in account and events URL (docs/contracts/account-events.md).
    private AccountEventsClient? _events;
    private string? _eventsKey;
    private string? _eventsToken;
    private bool _stopped;
    private bool _accountSuspended;
    private bool _tickleRefreshRunning;
    private bool _tickleRefreshQueued;
    // WP-354: the PKCE verifier of the sign-in that just finished, sent once with the next bootstrap so the server can
    // find the sign-in page's ticket and grant the signup credits. Cleared once the server answered, or at sign-out.
    private string? _signupVerifier;
    private string? _signupGrant;
    // WP-390: the same verifier on disk until the Gateway answers, so closing the app after a failed bootstrap cannot
    // lose the grant, and the loop that retries a failed bootstrap without waiting for someone to read the balance.
    private readonly ISignupVerifierStore _pendingVerifier;
    private CancellationTokenSource? _bootstrapRetry;
    private string? _bootstrapRetryUser;

    public AccountSessionService(IAccountTokenStore? store = null, ISignupVerifierStore? pendingVerifier = null)
    {
        _store = store ?? new DpapiAccountStore(() => ProjectUrl(HostedEndpoints.Current));
        _pendingVerifier = pendingVerifier ?? new DpapiSignupVerifierStore(() => ProjectUrl(HostedEndpoints.Current));
    }

    public bool IsSignedIn
    {
        get { lock (_gate) return _bundle is { AccessToken.Length: > 0, UserId.Length: > 0 }; }
    }

    public string? UserId
    {
        get { lock (_gate) return _bundle?.UserId; }
    }

    public string? Email
    {
        get { lock (_gate) return _bundle?.Email; }
    }

    /// <summary>
    /// The access token hosted clients send, or null. Null, and the session dropped, when the
    /// configured project is no longer the one that issued it.
    /// </summary>
    public string? AccessToken => BundleFor(HostedEndpoints.Current)?.AccessToken;

    public string? DisplayName
    {
        get { lock (_gate) return _bundle?.DisplayName; }
    }

    public string? AvatarUrl
    {
        get { lock (_gate) return _bundle?.AvatarUrl; }
    }

    public CreditBalance? Balance
    {
        get { lock (_gate) return _balance; }
    }

    public string? BalanceError
    {
        get { lock (_gate) return _balanceError; }
    }

    public bool SignInInProgress { get; private set; }

    /// <summary>
    /// What the bootstrap after the last sign-in did about the signup credits (applied, duplicate, no_ticket, ...), or
    /// null before it answered. <see cref="GoogleSignInFlow.GrantNote"/> turns it into the app's sentence.
    /// </summary>
    public string? SignupGrant
    {
        get { lock (_gate) return _signupGrant; }
    }

    /// <summary>
    /// Tickles are arriving, over the socket or the fallback poll. The Account panel's credit watch then waits for
    /// them, not for its own timers. (The job watchers slow down only for a live socket: HostedJobPush.)
    /// </summary>
    public bool PushLive => _events?.State is AccountEventsState.Live or AccountEventsState.Polling;

    /// <summary>The events Worker closed this account's sockets as suspended (ADR-007 §7). Cleared by the next sign-in.</summary>
    public bool AccountSuspended
    {
        get { lock (_gate) return _accountSuspended; }
    }

    public event EventHandler? Changed;

    /// <summary>Something changed on the account (WP-350), raised on the UI thread after the balance re-read began.</summary>
    public event EventHandler<AccountEventsTickle>? AccountTickled;

    public void Start()
    {
        HostedRuntime.Session = this;
        _dispatcher ??= Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _ = RestoreAsync();
        if (_refreshTimer is null && _dispatcher is { } dispatcher)
        {
            _refreshTimer = dispatcher.CreateTimer();
            _refreshTimer.Interval = TimeSpan.FromSeconds(30);
            // Each tick also checks the session against the configured project (RefreshIfNeededCoreAsync).
            _refreshTimer.Tick += async (_, _) =>
            {
                try { await RefreshIfNeededAsync(_lifetime.Token); }
                catch (Exception) { /* Foreground balance refresh reports errors; the timer retries. */ }
                // A developer file that changed its events URL moves the push client too (WP-350).
                SyncEvents();
            };
            _refreshTimer.Start();
        }
    }

    public void Stop()
    {
        _refreshTimer?.Stop();
        lock (_gate)
        {
            _stopped = true;
            StopBootstrapRetryLocked();
        }
        _lifetime.Cancel();
        CancelSignIn();
        SyncEvents();
    }

    public async Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        // The store keys by the project it reads now; a change between these two reads leaves the
        // session bound to the earlier project, and the next check drops it.
        var origin = AccountStoreKey.Origin(ProjectUrl(HostedEndpoints.Current));
        AccountTokenBundle? loaded;
        lock (_gate)
            loaded = _store.Load();
        if (loaded is null || origin is null)
        {
            RaiseChanged();
            return;
        }

        Apply(loaded, origin);
        await RefreshIfNeededAsync(cancellationToken).ConfigureAwait(true);
        await RestoreProfileAsync(cancellationToken).ConfigureAwait(true);
        await RefreshBalanceAsync(cancellationToken).ConfigureAwait(true);
    }

    private async Task RestoreProfileAsync(CancellationToken cancellationToken)
    {
        var backend = HostedEndpoints.Current;
        var bundle = BundleFor(backend);
        if (bundle is null || !backend.AuthConfigured
            || (bundle.DisplayName is not null && bundle.AvatarUrl is not null))
            return;

        try
        {
            var auth = new SupabaseAuthClient(backend.SupabaseUrl, backend.AnonKey);
            var profile = await auth.ReadProfileAsync(bundle.AccessToken, cancellationToken).ConfigureAwait(true);
            lock (_gate)
            {
                // A delayed profile from the previous account cannot repaint a new session.
                if (!ReferenceEquals(_bundle, bundle) || profile?.UserId != bundle.UserId)
                    return;
                bundle.DisplayName = profile.DisplayName;
                bundle.AvatarUrl = profile.AvatarUrl;
                _store.Save(bundle);
            }
            RaiseChanged();
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException
            or System.Security.Cryptography.CryptographicException)
        {
            // Profile decoration is optional; an offline photo must not discard the session.
        }
    }

    public async Task<SignInOutcome> SignInWithGoogleAsync(
        IProgress<string>? status = null,
        CancellationToken cancellationToken = default)
    {
        CancelSignIn();
        // One reading of the backend for the whole sign-in; the result is kept only if the same
        // project is still configured when Google returns.
        var backend = HostedEndpoints.Current;
        if (!backend.AuthConfigured || AccountStoreKey.Origin(backend.SupabaseUrl) is not { } origin)
        {
            return new SignInOutcome(
                SignInOutcomeKind.Rejected,
                Detail: HostedEndpoints.NotConfiguredMessage);
        }

        var redirect = backend.RedirectUri;
        if (!HostedEndpoints.TryParseLoopbackRedirect(redirect, out var port, out var path))
        {
            return new SignInOutcome(
                SignInOutcomeKind.Rejected,
                Detail: "The redirect URI must be a query-free http://127.0.0.1 loopback URL.");
        }

        LoopbackCallbackServer loopback;
        try
        {
            loopback = new LoopbackCallbackServer(port);
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or InvalidOperationException)
        {
            return new SignInOutcome(
                SignInOutcomeKind.Unknown,
                Detail: $"Could not listen on 127.0.0.1:{port}.");
        }

        _signInCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = _signInCts.Token;
        _loopback = loopback;
        SignInInProgress = true;
        RaiseChanged();
        try
        {
            var intent = _flow.Begin(redirect, GoogleSignInFlow.Lifetime);
            var auth = new SupabaseAuthClient(backend.SupabaseUrl, backend.AnonKey);
            // WP-354: Temno's sign-in page first (Turnstile, then Google with the same challenge). Without an accounts
            // host the browser goes to Google directly, and the account works without the signup credits.
            var url = (backend.AccountsConfigured
                    ? GoogleSignInFlow.StartPageUrl(backend.AccountsUrl, intent.Challenge, redirect)
                    : null)
                ?? auth.AuthorizeUrl(intent.Challenge, redirect);
            status?.Report("Waiting for Google in the browser…");
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            var callback = await loopback.WaitAsync(path, GoogleSignInFlow.Lifetime, ct).ConfigureAwait(true);
            var outcome = await _flow.CompleteAsync(callback, auth, ct).ConfigureAwait(true);
            if (outcome.Kind == SignInOutcomeKind.Exchanged && outcome.Session is { } session)
            {
                // The developer file switched projects while the browser was open: the session
                // belongs to the old project, so it is neither applied nor stored.
                if (!AccountStoreKey.SameProject(origin, ProjectUrl(HostedEndpoints.Current)))
                    return new SignInOutcome(SignInOutcomeKind.Rejected, Detail: ProjectChangedDuringSignInMessage);
                _store.Save(session);
                // The verifier belongs to this session only, so it is set in the same lock as the session.
                Apply(session, origin, intent.Verifier);
                await RefreshBalanceAsync(ct).ConfigureAwait(true);
            }

            return outcome.Kind == SignInOutcomeKind.Exchanged
                ? outcome
                : new SignInOutcome(outcome.Kind, Detail: DescribeSignIn(outcome, callback));
        }
        catch (OperationCanceledException)
        {
            _flow.Cancel();
            return new SignInOutcome(SignInOutcomeKind.Cancelled, Detail: "cancelled");
        }
        finally
        {
            SignInInProgress = false;
            loopback.Dispose();
            if (ReferenceEquals(_loopback, loopback))
                _loopback = null;
            RaiseChanged();
        }
    }

    public void CancelSignIn()
    {
        _flow.Cancel();
        try { _signInCts?.Cancel(); } catch { /* ignore */ }
        _signInCts?.Dispose();
        _signInCts = null;
        _loopback?.Dispose();
        _loopback = null;
        SignInInProgress = false;
        RaiseChanged();
    }

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        CancelSignIn();
        var backend = HostedEndpoints.Current;
        var bundle = BundleFor(backend, out var dropped);
        if (dropped)
            return; // The project changed: the session is gone from memory, and neither store is touched.

        var origin = AccountStoreKey.Origin(ProjectUrl(backend));
        var token = bundle?.AccessToken;
        if (!string.IsNullOrWhiteSpace(token) && backend.AuthConfigured)
        {
            try
            {
                var auth = new SupabaseAuthClient(backend.SupabaseUrl, backend.AnonKey);
                await auth.LogoutAsync(token, cancellationToken).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // Local sign-out still proceeds.
            }
        }

        var stillCurrent = AccountStoreKey.SameProject(origin, ProjectUrl(HostedEndpoints.Current));
        lock (_gate)
        {
            // A project change during the logout call leaves the new project's stored session alone.
            if (stillCurrent)
            {
                _store.Clear();
                _pendingVerifier.Clear();
            }
            DropLocked();
        }

        RaiseChanged();
    }

    public async Task RefreshBalanceAsync(CancellationToken cancellationToken = default)
    {
        await RefreshIfNeededAsync(cancellationToken).ConfigureAwait(true);
        var backend = HostedEndpoints.Current;
        var signedIn = BundleFor(backend);
        var token = signedIn?.AccessToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            lock (_gate)
            {
                _balance = null;
                _balanceError = null;
            }

            RaiseChanged();
            return;
        }

        if (!backend.AuthConfigured)
        {
            lock (_gate)
            {
                _balance = null;
                _balanceError = HostedEndpoints.NotConfiguredMessage;
            }

            RaiseChanged();
            return;
        }

        var origin = AccountStoreKey.Origin(backend.SupabaseUrl);
        CreditBalance? balance = null;
        string? error;
        // WP-354: one bootstrap carries the sign-in's verifier. It is taken here, so the balance reads that a sign-in
        // sets off in other panels cannot send it a second time (the server would answer ticket_used). If the call
        // fails it goes back, so the next read tries again: a refused or failed bootstrap spends no ticket.
        // It is taken only while the session whose token this call sends is still the current one, so a sign-in to
        // another account in between can never pair that account's verifier with this token.
        // WP-390: it is also taken only while it is this session's own pending verifier and under a day old; a stale
        // one is dropped. A failure that a later call could change starts the retry loop, and the copy on disk stays
        // until the Gateway answers.
        string? verifier = null;
        string? verifierOrigin = null;
        lock (_gate)
        {
            if (backend.GatewayConfigured && SameAccount(_bundle, signedIn) && _signupVerifier is { } held)
            {
                _signupVerifier = null;
                var pending = _pendingVerifier.Load();
                if (pending is not null && string.Equals(pending.Verifier, held, StringComparison.Ordinal)
                    && SignupVerifierRules.Belongs(pending, signedIn!.UserId, _bundleOrigin, DateTimeOffset.UtcNow))
                {
                    verifier = held;
                    verifierOrigin = _bundleOrigin;
                }
                else
                {
                    ForgetPendingVerifierLocked(held);
                }
            }
        }
        try
        {
            var client = new CreditBalanceClient(backend.SupabaseUrl, backend.AnonKey);
            if (verifier is not null)
            {
                // The first balance read after a sign-in is the bootstrap that carries its verifier, even for an
                // account that already exists: an account Billing provisioned first can still get its grant.
                AccountBootstrap bootstrap;
                try
                {
                    var gateway = new CreditBalanceClient(backend.SupabaseUrl, backend.AnonKey,
                        new HttpClient { Timeout = SignupVerifierRules.BootstrapTimeout });
                    bootstrap = await gateway.BootstrapSignInAsync(backend.GatewayUrl, token, verifier, cancellationToken)
                        .ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    // It goes back only to the session and project it was taken for, and only while it is still that
                    // sign-in's pending verifier: a project switch or a newer sign-in in between keeps theirs.
                    var restored = false;
                    lock (_gate)
                    {
                        if (!SignupVerifierRules.MayAnswerLater(ex))
                        {
                            ForgetPendingVerifierLocked(verifier);
                        }
                        else if (SameAccount(_bundle, signedIn) && _signupVerifier is null
                            && string.Equals(_bundleOrigin, verifierOrigin, StringComparison.Ordinal)
                            && string.Equals(_pendingVerifier.Load()?.Verifier, verifier, StringComparison.Ordinal))
                        {
                            _signupVerifier = verifier;
                            restored = true;
                        }
                    }
                    if (restored)
                        StartBootstrapRetry(signedIn!);
                    throw;
                }

                balance = bootstrap.Balance;
                lock (_gate)
                {
                    if (SameAccount(_bundle, signedIn))
                    {
                        _signupGrant = bootstrap.SignupGrant;
                        ForgetPendingVerifierLocked(verifier);
                    }
                }
            }
            else
            {
                balance = await client.ReadAsync(token, cancellationToken).ConfigureAwait(true);
                if (balance is null && backend.GatewayConfigured)
                    balance = await client.BootstrapAsync(backend.GatewayUrl, token, cancellationToken).ConfigureAwait(true);
            }
            error = balance is null
                ? "No hosted credit account yet. Hosted generation is optional."
                : null;
        }
        catch (Exception ex)
        {
            balance = null;
            error = HostedApiErrors.Describe(ex);
        }

        lock (_gate)
        {
            // A balance read for a project that is no longer the session's does not repaint.
            if (_bundle is not null && string.Equals(_bundleOrigin, origin, StringComparison.Ordinal))
            {
                _balance = balance;
                _balanceError = error;
            }
        }

        RaiseChanged();
    }

    private async Task RefreshIfNeededAsync(CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try { await RefreshIfNeededCoreAsync(cancellationToken).ConfigureAwait(true); }
        finally { _refreshGate.Release(); }
    }

    private async Task RefreshIfNeededCoreAsync(CancellationToken cancellationToken)
    {
        // Checked before the expiry, so every timer tick drops a session whose project changed.
        var backend = HostedEndpoints.Current;
        var bundle = BundleFor(backend);
        if (bundle is null || string.IsNullOrWhiteSpace(bundle.RefreshToken))
            return;
        var skew = TimeSpan.FromSeconds(60);
        if (bundle.AccessExpiresAt is DateTimeOffset exp && exp - skew > DateTimeOffset.UtcNow)
            return;

        if (!backend.AuthConfigured)
            return;
        var origin = AccountStoreKey.Origin(backend.SupabaseUrl);
        var auth = new SupabaseAuthClient(backend.SupabaseUrl, backend.AnonKey);
        var outcome = await auth.RefreshAsync(bundle.RefreshToken, cancellationToken).ConfigureAwait(true);
        var stillCurrent = AccountStoreKey.SameProject(origin, ProjectUrl(HostedEndpoints.Current));
        bool changed = false;
        lock (_gate)
        {
            // A sign-out or another sign-in must win over an old in-flight refresh.
            if (!ReferenceEquals(_bundle, bundle)) return;
            if (!stillCurrent)
            {
                // The project changed during the refresh: keep neither the result nor the session,
                // and leave both projects' stored sessions alone.
                DropLocked();
                changed = true;
            }
            else if (outcome.Kind == SignInOutcomeKind.Exchanged && outcome.Session is { } refreshed)
            {
                _store.Save(refreshed);
                _bundle = refreshed;
                changed = true;
            }
            else if (outcome.Kind is SignInOutcomeKind.Rejected or SignInOutcomeKind.Expired)
            {
                _store.Clear();
                _pendingVerifier.Clear();
                DropLocked();
                _balanceError = "Sign in again.";
                changed = true;
            }
        }
        if (changed) RaiseChanged();
    }

    /// <summary>The Supabase URL sign-in uses with <paramref name="backend"/>, or null when sign-in is off.</summary>
    private static string? ProjectUrl(HostedBackend backend)
        => backend.AuthConfigured ? backend.SupabaseUrl : null;

    private AccountTokenBundle? BundleFor(HostedBackend backend) => BundleFor(backend, out _);

    /// <summary>
    /// The session when it belongs to <paramref name="backend"/>'s project. Otherwise the session
    /// is dropped from memory (<paramref name="dropped"/>), the change is announced, and null is
    /// returned; no stored session is touched.
    /// </summary>
    private AccountTokenBundle? BundleFor(HostedBackend backend, out bool dropped)
    {
        var current = ProjectUrl(backend);
        AccountTokenBundle? bundle;
        dropped = false;
        lock (_gate)
        {
            bundle = _bundle;
            if (bundle is not null && !AccountStoreKey.SameProject(_bundleOrigin, current))
            {
                DropLocked();
                bundle = null;
                dropped = true;
            }
        }

        if (dropped)
            RaiseChangedOnUi();
        return bundle;
    }

    /// <summary>The same signed-in account: a token refresh replaces the bundle, not the account.</summary>
    private static bool SameAccount(AccountTokenBundle? current, AccountTokenBundle? other) =>
        current is not null && other is not null && string.Equals(current.UserId, other.UserId, StringComparison.Ordinal);

    /// <summary>Signed out in memory. Call under <see cref="_gate"/>.</summary>
    private void DropLocked()
    {
        _bundle = null;
        _bundleOrigin = null;
        _balance = null;
        _balanceError = null;
        _signupVerifier = null;
        _signupGrant = null;
        StopBootstrapRetryLocked();
    }

    private void Apply(AccountTokenBundle bundle, string origin, string? signupVerifier = null)
    {
        lock (_gate)
        {
            _bundle = bundle;
            _bundleOrigin = origin;
            _accountSuspended = false;
            // WP-354: a new session carries only its own sign-in's verifier, never a previous one's. WP-390: a restored
            // session takes back the verifier its own sign-in left on disk; anyone else's is deleted unsent.
            // A record of another project (the developer file switched during a restore) is neither used nor deleted.
            var now = DateTimeOffset.UtcNow;
            if (signupVerifier is not null)
                _pendingVerifier.Save(new PendingSignupVerifier(bundle.UserId, origin, signupVerifier, now));
            else if (_pendingVerifier.Load() is { } kept && string.Equals(kept.Origin, origin, StringComparison.Ordinal)
                && !SignupVerifierRules.Belongs(kept, bundle.UserId, origin, now))
                _pendingVerifier.Clear();
            _signupVerifier = signupVerifier
                ?? (_pendingVerifier.Load() is { } own && SignupVerifierRules.Belongs(own, bundle.UserId, origin, now)
                    ? own.Verifier
                    : null);
            _signupGrant = null;
            if (_bootstrapRetryUser is not null && !string.Equals(_bootstrapRetryUser, bundle.UserId, StringComparison.Ordinal))
                StopBootstrapRetryLocked();
        }
        RaiseChanged();
    }

    /// <summary>The Gateway answered, or refused for good: the copy on disk goes, unless a newer sign-in replaced it.</summary>
    private void ForgetPendingVerifierLocked(string verifier)
    {
        if (string.Equals(_pendingVerifier.Load()?.Verifier, verifier, StringComparison.Ordinal))
            _pendingVerifier.Clear();
    }

    /// <summary>
    /// WP-390: after a bootstrap failed in a way a later call could change, keep trying without waiting for a balance read,
    /// while the ticket may live. One loop per signed-in account; it ends when the Gateway answers or refuses, the account
    /// changes or signs out, the ticket's 15 minutes pass, or the app stops. A verifier still pending then goes out with
    /// the next read or start.
    /// </summary>
    private void StartBootstrapRetry(AccountTokenBundle session)
    {
        CancellationTokenSource loop;
        CancellationToken token;
        lock (_gate)
        {
            if (_stopped || _lifetime.IsCancellationRequested || !SameAccount(_bundle, session)
                || string.Equals(_bootstrapRetryUser, session.UserId, StringComparison.Ordinal))
                return;
            StopBootstrapRetryLocked();
            loop = new CancellationTokenSource();
            token = loop.Token;
            _bootstrapRetry = loop;
            _bootstrapRetryUser = session.UserId;
        }
        _ = RetryBootstrapAsync(session, loop, token);
    }

    private async Task RetryBootstrapAsync(AccountTokenBundle session, CancellationTokenSource loop, CancellationToken token)
    {
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                await Task.Delay(SignupVerifierRules.RetryGap(attempt), token).ConfigureAwait(true);
                lock (_gate)
                {
                    if (!SameAccount(_bundle, session) || _pendingVerifier.Load() is not { } pending
                        || !SignupVerifierRules.Belongs(pending, session.UserId, _bundleOrigin, DateTimeOffset.UtcNow)
                        || !SignupVerifierRules.Retrying(pending, DateTimeOffset.UtcNow))
                        return;
                }
                try { await RefreshBalanceAsync(token).ConfigureAwait(true); }
                catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
                {
                    // RefreshBalanceAsync reports its own error; the next gap tries again.
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Signed out, another account, or the app is closing: a verifier still pending stays on disk for the next start.
        }
        finally
        {
            // A loop that ended by itself is disposed here; a stopped one by StopBootstrapRetryLocked, after its Cancel,
            // which may run this continuation inline.
            bool ended;
            lock (_gate)
            {
                ended = ReferenceEquals(_bootstrapRetry, loop);
                if (ended)
                {
                    _bootstrapRetry = null;
                    _bootstrapRetryUser = null;
                }
            }
            if (ended)
                loop.Dispose();
        }
    }

    private void StopBootstrapRetryLocked()
    {
        var loop = _bootstrapRetry;
        _bootstrapRetry = null;
        _bootstrapRetryUser = null;
        if (loop is null)
            return;
        loop.Cancel();
        loop.Dispose();
    }

    private void RaiseChanged()
    {
        SyncEvents();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Keeps one push client for the signed-in account and the configured events URL, and tells it when the access
    /// token changed. Runs with every change of the session, so sign-out, a project switch and Stop end it.
    /// </summary>
    private void SyncEvents()
    {
        if (_dispatcher is { HasThreadAccess: false } dispatcher)
        {
            dispatcher.TryEnqueue(SyncEvents);
            return;
        }

        var backend = HostedEndpoints.Current;
        string? key = null;
        string? token = null;
        lock (_gate)
        {
            if (!_stopped && !_accountSuspended && _bundle is { UserId.Length: > 0 } bundle
                && backend.EventsConfigured && AccountStoreKey.SameProject(_bundleOrigin, ProjectUrl(backend)))
            {
                key = bundle.UserId + " " + backend.EventsUrl;
                token = bundle.AccessToken;
            }
        }

        if (key == _eventsKey)
        {
            if (_events is { } open && token != _eventsToken)
            {
                _eventsToken = token;
                open.TokenChanged();
            }
            return;
        }

        if (_events is { } old)
        {
            old.Tickled -= OnTickled;
            old.StateChanged -= OnEventsState;
            _ = old.StopAsync();
            HostedJobPush.Live = false;
        }

        _events = null;
        _eventsKey = key;
        _eventsToken = token;
        if (key is null || !Uri.TryCreate(backend.EventsUrl, UriKind.Absolute, out var eventsUrl))
            return;
        var client = new AccountEventsClient(eventsUrl, () => AccessToken,
            new ClientWebSocketTransport(Temno.Environments.TemnoEnvironments.Name(HostedEndpoints.BuildEnvironment)));
        client.Tickled += OnTickled;
        client.StateChanged += OnEventsState;
        _events = client;
        client.Start();
    }

    /// <summary>A tickle: re-read what it names. Postgres stays the truth; the tickle says only what changed.</summary>
    private void OnTickled(AccountEventsTickle tickle)
    {
        if (tickle.Jobs)
            HostedWatcherRegistry.Shared.WakeAll();
        _dispatcher?.TryEnqueue(() =>
        {
            if (tickle.Credits)
                _ = RefreshBalanceForTickleAsync();
            AccountTickled?.Invoke(this, tickle);
        });
    }

    private void OnEventsState(AccountEventsState state)
    {
        HostedJobPush.Live = state == AccountEventsState.Live && _events?.JobEventsLive == true;
        if (state != AccountEventsState.Suspended)
            return;
        _dispatcher?.TryEnqueue(() =>
        {
            lock (_gate)
                _accountSuspended = true;
            RaiseChanged();
        });
    }

    /// <summary>One balance read at a time; a burst of tickles while it runs costs one more read, not one each.</summary>
    private async Task RefreshBalanceForTickleAsync()
    {
        if (_tickleRefreshRunning)
        {
            _tickleRefreshQueued = true;
            return;
        }

        _tickleRefreshRunning = true;
        try
        {
            do
            {
                _tickleRefreshQueued = false;
                try { await RefreshBalanceAsync(_lifetime.Token).ConfigureAwait(true); }
                catch (Exception) { /* the next tickle or the panel's own refresh tries again */ }
            }
            while (_tickleRefreshQueued && !_lifetime.IsCancellationRequested);
        }
        finally
        {
            _tickleRefreshRunning = false;
        }
    }

    /// <summary>Hosted clients read the token off the UI thread; the change is announced on it.</summary>
    private void RaiseChangedOnUi()
    {
        if (_dispatcher is { HasThreadAccess: false } dispatcher)
            dispatcher.TryEnqueue(RaiseChanged);
        else
            RaiseChanged();
    }

    private static string DescribeSignIn(SignInOutcome outcome, CallbackResult callback)
    {
        if (!string.IsNullOrWhiteSpace(outcome.Detail) && outcome.Detail is not (
            "no_intent" or "replay" or "expired" or "cancelled" or "auth_code_rejected"
            or "auth_transport_lost" or "auth_unavailable" or "no_session" or "missing_code"
            or "provider_error"))
            return outcome.Detail!;
        return outcome.Kind switch
        {
            SignInOutcomeKind.Cancelled => "Sign-in cancelled.",
            SignInOutcomeKind.Expired => "Sign-in timed out. The Google redirect URI may not be allowlisted yet.",
            SignInOutcomeKind.Replay => "That sign-in link was already used.",
            SignInOutcomeKind.Rejected => "Google sign-in did not complete.",
            _ => "Sign-in did not complete.",
        };
    }
}
