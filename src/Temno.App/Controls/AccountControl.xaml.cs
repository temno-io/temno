using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Temno.App.Controls.Shared;
using Temno.App.Services;
using Temno.App.Theme;
using Temno.Core.Account;
using Temno.Environments;

namespace Temno.App.Controls;

public sealed partial class AccountControl : UserControl
{
    private readonly BillingClient _billing = new();
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _busyDelay;
    private bool _busy;
    private bool _billingStarted;
    private bool _billingKnown;
    private bool _plansShown;
    private bool _awaitingReturn;
    private bool _leftForBilling;
    private bool _checkoutPending;
    private bool _windowHooked;
    private int _billingGeneration;
    private long? _balanceBeforeGrant;
    private string? _billingCode;
    private string _planStamp = "";
    private IReadOnlyList<BillingPlanOffer> _plans = Array.Empty<BillingPlanOffer>();
    private CreditPackOffer? _topUp;
    private CreditPackOffer? _pendingPack;
    private CreditPackOffer? _topUpItemsFor;
    private int _topUpQuantity = 1;
    private bool _fillingTopUpAmounts;
    private bool _topUpBeforeKnown;
    private TopUpGrantMark? _topUpBefore;
    private IReadOnlyList<TopUpGrantMark> _topUpRecent = [];
    private long _topUpAmount;
    private AccountSubscription? _subscription;
    private AccountSubscription? _subscriptionBeforeReturn;
    private CancellationTokenSource? _grantWatch;
    // Ends the credit watch's current wait when a credits or plan tickle arrives (WP-350); a tickle during a read
    // makes the next wait zero.
    private CancellationTokenSource? _tickleWait;
    private bool _tickledSinceRead;

    public bool ShowHeading
    {
        get => HeadingText.Visibility == Visibility.Visible;
        set => HeadingText.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    public AccountControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        Render();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AccountSessionService.Current.Changed += OnSessionChanged;
        AccountSessionService.Current.AccountTickled += OnAccountTickled;
        HookWindow(true);
        if (AccountSessionService.Current.IsSignedIn)
            StartBillingLoad();
        Render();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        AccountSessionService.Current.Changed -= OnSessionChanged;
        AccountSessionService.Current.AccountTickled -= OnAccountTickled;
        HookWindow(false);
        ResetBilling();
    }

    private void OnSessionChanged(object? sender, EventArgs e)
    {
        if (!AccountSessionService.Current.IsSignedIn)
            ResetBilling();
        else if (!_billingKnown)
            StartBillingLoad();
        DispatcherQueue.TryEnqueue(Render);
    }

    /// <summary>On the UI thread. A credits or plan change ends the watch's wait, so it reads at once.</summary>
    private void OnAccountTickled(object? sender, Temno.Core.Account.AccountEventsTickle tickle)
    {
        if (!tickle.Credits)
            return;
        _tickledSinceRead = true;
        _tickleWait?.Cancel();
    }

    private void HookWindow(bool on)
    {
        if (App.MainAppWindow is not { } window)
            return;
        if (on && !_windowHooked)
        {
            window.Activated += OnWindowActivated;
            _windowHooked = true;
        }
        else if (!on && _windowHooked)
        {
            window.Activated -= OnWindowActivated;
            _windowHooked = false;
        }
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs e)
    {
        if (!_awaitingReturn)
            return;
        if (e.WindowActivationState == WindowActivationState.Deactivated)
        {
            _leftForBilling = true;
            // With push (WP-350) the watch only waits for tickles while the user pays, so it keeps running and
            // "credits arrived" shows the moment the grant lands. Without push it stops, and re-arms on return.
            if (AccountSessionService.Current.PushLive)
            {
                if (_grantWatch is null)
                    BeginGrantWatch();
                return;
            }
            _grantWatch?.Cancel();
            _grantWatch = null;
            return;
        }
        if (_leftForBilling && _grantWatch is null)
            BeginGrantWatch();
        else if (_grantWatch is not null)
        {
            // Coming back reads at once, in case a tickle was lost.
            _tickledSinceRead = true;
            _tickleWait?.Cancel();
        }
    }

    private void Render()
    {
        var session = AccountSessionService.Current;
        var signedIn = session.IsSignedIn;
        SignedOutPane.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
        SignedInPane.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
        CancelSignInButton.Visibility = session.SignInInProgress ? Visibility.Visible : Visibility.Collapsed;
        SignInButton.IsEnabled = !session.SignInInProgress && !_busy;
        ProfileAvatar.Update(session.UserId, session.DisplayName, session.Email, session.AvatarUrl);
        DisplayNameText.Text = signedIn ? session.DisplayName ?? "Google account" : "";
        IdentityText.Text = signedIn ? session.Email ?? "Signed in" : "";
        var backend = HostedEndpoints.Current;
        if (backend.Environment == TemnoEnvironment.Development)
        {
            EnvironmentText.Text = backend.AuthConfigured && Uri.TryCreate(backend.SupabaseUrl, UriKind.Absolute, out var project)
                ? "Development backend · " + project.Host
                : "Development backend";
            EnvironmentText.Visibility = Visibility.Visible;
        }
        else
            EnvironmentText.Visibility = Visibility.Collapsed;

        if (!backend.AuthConfigured)
            SignedOutHint.Text = HostedEndpoints.NotConfiguredMessage;
        else
            SignedOutHint.Text = "Sign in to use your Temno credits and generate media.";

        if (signedIn)
        {
            if (session.AccountSuspended)
            {
                BalanceText.Text = Temno.Core.Generation.HostedJobPhases.SuspendedMessage;
                BalanceText.Foreground = AppTheme.Status.ErrorBrush;
            }
            else if (session.Balance is { } balance)
            {
                BalanceText.Text = balance.OwnerTesting
                    ? "Owner testing · development credits are supplied automatically."
                    : $"Available {balance.Available} credits · reserved {balance.Reserved} · total {balance.Total}.";
                BalanceText.Foreground = AppTheme.Text.SecondaryBrush;
            }
            else if (!string.IsNullOrWhiteSpace(session.BalanceError))
            {
                BalanceText.Text = session.BalanceError;
                BalanceText.Foreground = session.BalanceError.Contains("credit account", StringComparison.OrdinalIgnoreCase)
                    ? AppTheme.Text.SecondaryBrush
                    : AppTheme.Status.ErrorBrush;
            }
            else
            {
                BalanceText.Text = "Balance not loaded yet.";
                BalanceText.Foreground = AppTheme.Text.TertiaryBrush;
            }

            // WP-354: the signup credits this sign-in brought, or why it brought none. Drawn from the session on every
            // render, so a redraw or a reopened panel keeps it; it lasts until sign-out.
            var grant = session.SignupGrant;
            var note = GoogleSignInFlow.GrantNote(grant);
            SignupGrantText.Text = note ?? "";
            SignupGrantText.Foreground = grant == "applied" ? AppTheme.Status.SuccessBrush : AppTheme.Text.SecondaryBrush;
            SignupGrantText.Visibility = note is null ? Visibility.Collapsed : Visibility.Visible;

            RenderBilling();
        }

        if (!session.SignInInProgress && !_busy && string.IsNullOrWhiteSpace(StatusText.Text))
            StatusText.Text = signedIn ? "" : "";
    }

    private void RenderBilling()
    {
        var subscribed = _subscription?.IsCurrent == true || _billingCode == "already_subscribed";
        if (!_billingKnown)
            PlanText.Text = "Loading your plan…";
        else if (_subscription is { } current)
        {
            var offer = _plans.FirstOrDefault(plan => plan.TierKey == current.TierKey);
            PlanText.Text = current.Summary(offer);
        }
        else
            PlanText.Text = "No plan yet.";
        PlanText.Foreground = AppTheme.Text.SecondaryBrush;

        ChoosePlanButton.Visibility = subscribed ? Visibility.Collapsed : Visibility.Visible;
        var accent = Application.Current.Resources["AccentButtonStyle"] as Style;
        ChoosePlanButton.Style = subscribed ? null : accent;
        ManageBillingButton.Style = subscribed ? accent : null;
        SetEnabled(ChoosePlanButton, !_busy);
        SetEnabled(ManageBillingButton, !_busy);
        SetEnabled(RetryPlansButton, !_busy);

        if (!string.IsNullOrEmpty(_billingCode))
        {
            BillingNotice.Text = BillingSurfaceCopy.For(_billingCode);
            BillingNotice.Foreground = BrushFor(_billingCode);
            BillingNotice.Visibility = Visibility.Visible;
        }

        RetryPlansButton.Visibility = BillingSurfaceCopy.IsNetwork(_billingCode) || _billingCode == "billing_not_active"
            ? Visibility.Visible
            : Visibility.Collapsed;
        var showPlans = _plansShown && !subscribed && _billingCode is null;
        PlanList.Visibility = showPlans ? Visibility.Visible : Visibility.Collapsed;
        PlansEmpty.Visibility = showPlans && _plans.Count == 0 && _billingKnown && _billingCode is null
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (showPlans)
            FillPlanCards();
        RenderTopUp();
    }

    private void RenderTopUp()
    {
        // already_subscribed without a row means the projection has not caught up: say nothing yet.
        if (!_billingKnown || _topUp is not { } offer || (_subscription is null && _billingCode == "already_subscribed"))
        {
            TopUpPane.Visibility = Visibility.Collapsed;
            return;
        }

        var entitled = CreditPackOffer.IsEntitled(_subscription);
        TopUpPane.Visibility = Visibility.Visible;
        TopUpBuyRow.Visibility = entitled ? Visibility.Visible : Visibility.Collapsed;
        if (entitled)
        {
            TopUpText.Text = offer.PriceLine();
            FillTopUpAmounts(offer);
            LabelBuyCredits(offer);
        }
        else
            TopUpText.Text = _subscription is null ? BillingSurfaceCopy.TopUpNeedsPlan : BillingSurfaceCopy.TopUpNeedsActivePlan;
        SetEnabled(BuyCreditsButton, !_busy);
        SetEnabled(TopUpAmountBox, !_busy);
    }

    // One item per pack count, 1 to the host's maximum; rebuilt only when the offer changes.
    private void FillTopUpAmounts(CreditPackOffer offer)
    {
        _topUpQuantity = Math.Clamp(_topUpQuantity, 1, offer.MaxQuantity);
        if (offer != _topUpItemsFor)
        {
            _fillingTopUpAmounts = true;
            try
            {
                TopUpAmountBox.Items.Clear();
                for (var quantity = 1; quantity <= offer.MaxQuantity; quantity++)
                    TopUpAmountBox.Items.Add(new ComboBoxItem { Content = offer.AmountLabel(quantity), Tag = quantity });
                _topUpItemsFor = offer;
            }
            finally
            {
                _fillingTopUpAmounts = false;
            }
        }

        if (TopUpAmountBox.SelectedIndex != _topUpQuantity - 1)
        {
            _fillingTopUpAmounts = true;
            TopUpAmountBox.SelectedIndex = _topUpQuantity - 1;
            _fillingTopUpAmounts = false;
        }
    }

    private void LabelBuyCredits(CreditPackOffer offer)
    {
        var label = offer.ActionLabel(_topUpQuantity);
        BuyCreditsButton.Content = label;
        AutomationProperties.SetName(BuyCreditsButton, label);
    }

    private void TopUpAmount_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingTopUpAmounts || TopUpAmountBox.SelectedItem is not ComboBoxItem { Tag: int quantity } || _topUp is not { } offer)
            return;
        _topUpQuantity = quantity;
        LabelBuyCredits(offer);
    }

    private void FillPlanCards()
    {
        var stamp = string.Join("|", _plans.Select(plan => plan.TierKey + ":" + plan.UnitAmount)) + ":" + _busy;
        if (stamp == _planStamp && PlanList.Children.Count == _plans.Count)
            return;
        _planStamp = stamp;
        PlanList.Children.Clear();
        foreach (var plan in _plans)
        {
            var stack = new StackPanel { Spacing = Space("AppTheme.Spacing.Xs") };
            stack.Children.Add(new TextBlock
            {
                Text = plan.DisplayName,
                FontSize = Font("AppTheme.FontSize.MdLg"),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = AppTheme.Text.PrimaryBrush,
                TextWrapping = TextWrapping.WrapWholeWords,
            });
            stack.Children.Add(new TextBlock
            {
                Text = plan.PriceLine(),
                FontSize = Font("AppTheme.FontSize.SmMd"),
                Foreground = AppTheme.Text.SecondaryBrush,
                TextWrapping = TextWrapping.WrapWholeWords,
            });
            stack.Children.Add(new TextBlock
            {
                Text = plan.CreditsLine(),
                FontSize = Font("AppTheme.FontSize.SmMd"),
                Foreground = AppTheme.Text.TertiaryBrush,
                TextWrapping = TextWrapping.WrapWholeWords,
            });
            var subscribe = new Button
            {
                Content = "Subscribe",
                FontSize = Font("AppTheme.FontSize.SmMd"),
                Style = Application.Current.Resources["AccentButtonStyle"] as Style,
                Tag = plan.TierKey,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            subscribe.Click += Subscribe_Click;
            ToolTipService.SetToolTip(subscribe, "Open Stripe checkout for " + plan.DisplayName);
            AutomationProperties.SetName(subscribe, "Subscribe to " + plan.DisplayName);
            SetEnabled(subscribe, !_busy);
            stack.Children.Add(subscribe);

            PlanList.Children.Add(new Border
            {
                Background = AppTheme.Background.RaisedBrush,
                BorderBrush = AppTheme.Border.SubtleBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = Radius("AppTheme.CornerRadius.Md"),
                Padding = Pad("AppTheme.Padding.MdLgMd"),
                Child = stack,
            });
        }
    }

    private async void SignIn_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true, "Waiting for Google…");
        StatusText.Foreground = AppTheme.Text.TertiaryBrush;
        StatusText.Text = "Waiting for Google in the browser…";
        try
        {
            var outcome = await AccountSessionService.Current.SignInWithGoogleAsync();
            StatusText.Text = outcome.Kind switch
            {
                SignInOutcomeKind.Exchanged => "Signed in.",
                SignInOutcomeKind.Cancelled => "Sign-in cancelled.",
                _ => outcome.Detail ?? "Sign-in did not complete.",
            };
            StatusText.Foreground = outcome.Kind == SignInOutcomeKind.Exchanged
                ? AppTheme.Status.SuccessBrush
                : AppTheme.Status.ErrorBrush;
        }
        catch (Exception ex)
        {
            StatusText.Text = HostedApiErrors.Describe(ex);
            StatusText.Foreground = AppTheme.Status.ErrorBrush;
        }
        finally
        {
            SetBusy(false);
            if (AccountSessionService.Current.IsSignedIn)
                StartBillingLoad();
            Render();
        }
    }

    private void CancelSignIn_Click(object sender, RoutedEventArgs e)
    {
        AccountSessionService.Current.CancelSignIn();
        StatusText.Text = "Sign-in cancelled.";
        StatusText.Foreground = AppTheme.Text.TertiaryBrush;
        SetBusy(false);
        Render();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true, "Refreshing balance…");
        try
        {
            await QuietRefreshAsync(CancellationToken.None);
            ShowCheckoutUpdate();
            StatusText.Text = "";
        }
        catch (Exception ex)
        {
            StatusText.Text = HostedApiErrors.Describe(ex);
            StatusText.Foreground = AppTheme.Status.ErrorBrush;
        }
        finally
        {
            SetBusy(false);
            Render();
        }
    }

    private async void SignOut_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true, "Signing out…");
        try
        {
            await AccountSessionService.Current.SignOutAsync();
            ResetBilling();
            StatusText.Text = "Signed out.";
            StatusText.Foreground = AppTheme.Text.TertiaryBrush;
        }
        finally
        {
            SetBusy(false);
            Render();
        }
    }

    private async void ChoosePlan_Click(object sender, RoutedEventArgs e)
    {
        if (_billingCode == "billing_not_active")
        {
            ShowBilling(_billingCode);
            return;
        }

        if (_plans.Count == 0 || _billingCode is not null)
        {
            _billingKnown = false;
            _billingStarted = false;
            await LoadBillingAsync();
        }

        if (_billingCode == "billing_not_active")
            return;
        _plansShown = true;
        Render();
        if (PlanList.Visibility == Visibility.Visible)
        {
            PlanList.Opacity = 0;
            Motion.FadeIn(PlanList);
        }
    }

    private async void RetryPlans_Click(object sender, RoutedEventArgs e)
    {
        _billingKnown = false;
        _billingStarted = false;
        _billingCode = null;
        await LoadBillingAsync();
    }

    private async void Subscribe_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tier })
            return;
        await StartCheckoutAsync(tier);
    }

    private async Task StartCheckoutAsync(string tier)
    {
        if (_busy)
            return;
        var generation = _billingGeneration;
        var session = AccountSessionService.Current;
        var token = session.AccessToken;
        var accounts = HostedEndpoints.Current.AccountsUrl;
        if (string.IsNullOrEmpty(token) || !HostedEndpoints.Current.AccountsConfigured)
        {
            ShowBilling(string.IsNullOrEmpty(token) ? "unauthorized" : "accounts_not_configured");
            return;
        }

        SetBusy(true, "Opening checkout…");
        try
        {
            var start = await _billing.StartCheckoutAsync(accounts, token, tier);
            if (generation != _billingGeneration)
                return;
            _balanceBeforeGrant = session.Balance?.Total;
            _checkoutPending = true;
            _pendingPack = null;
            _awaitingReturn = true;
            _leftForBilling = false;
            ShowBilling(
                start.Reused ? "reused" : null,
                start.Reused ? BillingSurfaceCopy.ReusedCheckout : BillingSurfaceCopy.WaitingForGrant,
                calm: true);
            OpenStripe(start.Url);
        }
        catch (HostedApiException ex)
        {
            if (generation != _billingGeneration)
                return;
            ShowBilling(ex.Code);
            if (ex.Code == "already_subscribed")
                _plansShown = false;
        }
        catch (Exception ex)
        {
            ShowBilling(null, HostedApiErrors.Describe(ex), calm: false);
        }
        finally
        {
            if (generation == _billingGeneration)
            {
                SetBusy(false);
                Render();
            }
        }
    }

    private async void ManageBilling_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;
        var generation = _billingGeneration;
        var session = AccountSessionService.Current;
        var token = session.AccessToken;
        if (string.IsNullOrEmpty(token) || !HostedEndpoints.Current.AccountsConfigured)
        {
            ShowBilling(string.IsNullOrEmpty(token) ? "unauthorized" : "accounts_not_configured");
            Render();
            return;
        }

        SetBusy(true, "Opening billing…");
        try
        {
            var url = await _billing.StartPortalAsync(HostedEndpoints.Current.AccountsUrl, token);
            if (generation != _billingGeneration)
                return;
            _checkoutPending = false;
            _pendingPack = null;
            _balanceBeforeGrant = null;
            _subscriptionBeforeReturn = _subscription;
            _awaitingReturn = true;
            _leftForBilling = false;
            ShowBilling(null, "");
            OpenStripe(url);
        }
        catch (HostedApiException ex)
        {
            if (generation != _billingGeneration)
                return;
            ShowBilling(ex.Code);
        }
        catch (Exception ex)
        {
            ShowBilling(null, HostedApiErrors.Describe(ex), calm: false);
        }
        finally
        {
            if (generation == _billingGeneration)
            {
                SetBusy(false);
                Render();
            }
        }
    }

    private async void BuyCredits_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _topUp is not { } offer)
            return;
        var generation = _billingGeneration;
        var session = AccountSessionService.Current;
        var token = session.AccessToken;
        if (string.IsNullOrEmpty(token) || !HostedEndpoints.Current.AccountsConfigured)
        {
            ShowBilling(string.IsNullOrEmpty(token) ? "unauthorized" : "accounts_not_configured");
            Render();
            return;
        }

        SetBusy(true, "Opening checkout…");
        try
        {
            // The newest top-up grant before Checkout opens; only a newer one of this purchase's
            // amount confirms it.
            var backend = HostedEndpoints.Current;
            TopUpGrantMark? before = null;
            var beforeKnown = false;
            if (backend.AuthConfigured)
            {
                try
                {
                    before = (await _billing.ReadRecentTopUpGrantsAsync(backend.SupabaseUrl, backend.AnonKey, token))
                        .FirstOrDefault();
                    beforeKnown = true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The purchase still goes ahead; the panel just never claims it arrived.
                }
            }

            var quantity = Math.Clamp(_topUpQuantity, 1, offer.MaxQuantity);
            var start = await _billing.StartTopUpAsync(backend.AccountsUrl, token, quantity);
            if (generation != _billingGeneration)
                return;
            _balanceBeforeGrant = session.Balance?.Total;
            _checkoutPending = false;
            _pendingPack = offer;
            _topUpBefore = before;
            _topUpBeforeKnown = beforeKnown;
            _topUpRecent = [];
            _topUpAmount = offer.Credits * quantity;
            _subscriptionBeforeReturn = _subscription;
            _awaitingReturn = true;
            _leftForBilling = false;
            ShowBilling(
                start.Reused ? "reused" : null,
                start.Reused ? BillingSurfaceCopy.ReusedCheckout : BillingSurfaceCopy.WaitingForTopUp,
                calm: true);
            OpenStripe(start.Url);
        }
        catch (HostedApiException ex)
        {
            if (generation != _billingGeneration)
                return;
            ShowBilling(ex.Code);
        }
        catch (Exception ex)
        {
            ShowBilling(null, HostedApiErrors.Describe(ex), calm: false);
        }
        finally
        {
            if (generation == _billingGeneration)
            {
                SetBusy(false);
                Render();
            }
        }
    }

    private void BeginGrantWatch()
    {
        _grantWatch?.Cancel();
        var watch = new CancellationTokenSource();
        _grantWatch = watch;
        _ = WatchGrantAsync(watch, _billingGeneration);
    }

    private async Task WatchGrantAsync(CancellationTokenSource watch, int generation)
    {
        var cancellationToken = watch.Token;
        try
        {
            var waited = TimeSpan.Zero;
            for (var attempt = 0; ; attempt++)
            {
                if (attempt > 0)
                {
                    // With the push socket open (WP-350) a tickle ends the wait and the timer is only a safety poll.
                    // Without it, the gaps grow from four seconds as before. Either way the watch lasts ten minutes.
                    var gaps = Temno.Core.Generation.CheckoutWatchSchedule.Gaps();
                    var gap = AccountSessionService.Current.PushLive
                        ? Temno.Core.Generation.CheckoutWatchSchedule.PushGap
                        : attempt - 1 < gaps.Count ? gaps[attempt - 1] : TimeSpan.Zero;
                    if (gap <= TimeSpan.Zero || waited >= Temno.Core.Generation.CheckoutWatchSchedule.Budget)
                        break;
                    var started = DateTimeOffset.UtcNow;
                    if (!_tickledSinceRead)
                    {
                        using var tickle = new CancellationTokenSource();
                        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, tickle.Token);
                        _tickleWait = tickle;
                        try { await Task.Delay(gap, wait.Token); }
                        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { /* tickled */ }
                        finally
                        {
                            if (ReferenceEquals(_tickleWait, tickle))
                                _tickleWait = null;
                        }
                    }
                    waited += DateTimeOffset.UtcNow - started;
                }
                _tickledSinceRead = false;
                cancellationToken.ThrowIfCancellationRequested();
                if (!AccountSessionService.Current.IsSignedIn || generation != _billingGeneration)
                    return;
                try
                {
                    await QuietRefreshAsync(cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (generation != _billingGeneration)
                        return;
                    if (ShowCheckoutUpdate()
                        || (!_checkoutPending && _pendingPack is null && _subscription != _subscriptionBeforeReturn))
                    {
                        _awaitingReturn = false;
                        Render();
                        return;
                    }
                    // A refusal Billing just gave the user stays on screen (WP-342: it used to be
                    // overwritten within four seconds); this watch replaces only its own notices.
                    var refusalShown = _billingCode is not null and not "reused" && !BillingSurfaceCopy.IsNetwork(_billingCode);
                    if (!refusalShown)
                    {
                        if (_checkoutPending)
                            ShowBilling(null, BillingSurfaceCopy.WaitingForGrant, calm: true);
                        else if (_pendingPack is not null)
                            ShowBilling(null, BillingSurfaceCopy.WaitingForTopUp, calm: true);
                        else
                            ShowBilling(null, "");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (generation != _billingGeneration)
                        return;
                    ShowBilling("network_error");
                }
                Render();
            }
            _awaitingReturn = false;
        }
        catch (OperationCanceledException)
        {
            // Leaving the app re-arms a fresh bounded refresh on the next return.
        }
        finally
        {
            if (ReferenceEquals(_grantWatch, watch))
                _grantWatch = null;
            watch.Dispose();
        }
    }

    private bool ShowCheckoutUpdate()
    {
        var balance = AccountSessionService.Current.Balance;
        if (_pendingPack is not null && balance is { } current
            && CreditPackOffer.ArrivedGrant(_topUpBeforeKnown, _topUpBefore, _topUpRecent, _topUpAmount) is { } grant)
        {
            _pendingPack = null;
            _awaitingReturn = false;
            _balanceBeforeGrant = null;
            _topUpBefore = null;
            _topUpRecent = [];
            _topUpBeforeKnown = false;
            ShowBilling(null, BillingSurfaceCopy.TopUpArrived(grant.Amount, current.Available), calm: true, success: true);
            return true;
        }

        if (!_checkoutPending || _subscription?.HasCheckoutUpdate(_balanceBeforeGrant, balance) != true)
            return false;
        _checkoutPending = false;
        _awaitingReturn = false;
        _balanceBeforeGrant = null;
        ShowBilling(null, BillingSurfaceCopy.CheckoutUpdated(balance!.Value.Available), calm: true, success: true);
        return true;
    }

    private async Task QuietRefreshAsync(CancellationToken cancellationToken)
    {
        var generation = _billingGeneration;
        await AccountSessionService.Current.RefreshBalanceAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var session = AccountSessionService.Current;
        var token = session.AccessToken;
        var backend = HostedEndpoints.Current;
        if (string.IsNullOrEmpty(token) || !backend.AuthConfigured || generation != _billingGeneration)
            return;
        var subscription = await _billing.ReadSubscriptionAsync(backend.SupabaseUrl, backend.AnonKey, token, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (generation == _billingGeneration)
            _subscription = subscription;
        if (_pendingPack is null || !_topUpBeforeKnown)
            return;
        var recent = await _billing.ReadRecentTopUpGrantsAsync(backend.SupabaseUrl, backend.AnonKey, token, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (generation == _billingGeneration)
            _topUpRecent = recent;
    }

    private void StartBillingLoad()
    {
        if (_billingStarted || !AccountSessionService.Current.IsSignedIn)
            return;
        _billingStarted = true;
        _ = LoadBillingAsync();
    }

    private async Task LoadBillingAsync()
    {
        var generation = ++_billingGeneration;
        var session = AccountSessionService.Current;
        var token = session.AccessToken;
        var backend = HostedEndpoints.Current;
        if (string.IsNullOrEmpty(token))
        {
            _billingStarted = false;
            return;
        }

        SetBusy(true, "Loading your plan…");
        string? code = null;
        AccountSubscription? subscription = null;
        var subscriptionRead = false;
        BillingCatalog? catalog = null;
        try
        {
            if (!backend.AccountsConfigured)
                code = "accounts_not_configured";
            if (backend.AuthConfigured)
            {
                try
                {
                    subscription = await _billing.ReadSubscriptionAsync(backend.SupabaseUrl, backend.AnonKey, token);
                    subscriptionRead = true;
                }
                catch (HostedApiException ex)
                {
                    code ??= ex.Code ?? "network_error";
                }
            }

            if (backend.AccountsConfigured && code is null)
            {
                try
                {
                    catalog = await _billing.ListCatalogAsync(backend.AccountsUrl, token);
                }
                catch (HostedApiException ex)
                {
                    code = ex.Code ?? "network_error";
                }
            }
        }
        finally
        {
            if (generation == _billingGeneration)
            {
                if (subscriptionRead)
                    _subscription = subscription;
                if (catalog is not null)
                {
                    _plans = catalog.Plans;
                    _topUp = catalog.TopUp;
                }
                _billingCode = code;
                _billingKnown = true;
                if (code is not null)
                    ShowBilling(code);
                else if (!_awaitingReturn)
                {
                    BillingNotice.Text = "";
                    BillingNotice.Tag = null;
                    BillingNotice.Visibility = Visibility.Collapsed;
                }

                SetBusy(false);
                Render();
            }
        }
    }

    private void ResetBilling()
    {
        _billingGeneration++;
        _grantWatch?.Cancel();
        _grantWatch = null;
        _awaitingReturn = false;
        _leftForBilling = false;
        _checkoutPending = false;
        _pendingPack = null;
        _topUpBefore = null;
        _topUpRecent = [];
        _topUpBeforeKnown = false;
        _subscriptionBeforeReturn = null;
        _balanceBeforeGrant = null;
        SetBusy(false);
        _billingStarted = false;
        _billingKnown = false;
        _plansShown = false;
        _billingCode = null;
        _planStamp = "";
        _plans = Array.Empty<BillingPlanOffer>();
        _topUp = null;
        _subscription = null;
        BillingNotice.Text = "";
        BillingNotice.Tag = null;
        BillingNotice.Visibility = Visibility.Collapsed;
    }

    private void ShowBilling(string? code, string? text = null, bool calm = false, bool success = false)
    {
        _billingCode = code;
        BillingNotice.Tag = code;
        BillingNotice.Text = text ?? (code is null ? "" : BillingSurfaceCopy.For(code));
        BillingNotice.Foreground = success
            ? AppTheme.Status.SuccessBrush
            : code is null && !calm
                ? AppTheme.Status.ErrorBrush
                : BrushFor(code);
        BillingNotice.Visibility = string.IsNullOrEmpty(BillingNotice.Text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private static SolidColorBrush BrushFor(string? code)
    {
        if (BillingSurfaceCopy.IsNetwork(code))
            return AppTheme.Status.ErrorBrush;
        if (code == "already_subscribed")
            return AppTheme.Text.SecondaryBrush;
        return AppTheme.Text.TertiaryBrush;
    }

    private static void OpenStripe(string url)
    {
        if (!BillingClient.IsStripeUrl(url))
            throw new HostedApiException(BillingSurfaceCopy.For("invalid_billing_url"), code: "invalid_billing_url");
        Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
    }

    private void SetBusy(bool busy, string? label = null)
    {
        _busy = busy;
        SignInButton.IsEnabled = !busy && !AccountSessionService.Current.SignInInProgress;
        SetEnabled(RefreshButton, !busy);
        SetEnabled(SignOutButton, !busy);
        SetEnabled(ChoosePlanButton, !busy);
        SetEnabled(ManageBillingButton, !busy);
        SetEnabled(RetryPlansButton, !busy);
        SetEnabled(BuyCreditsButton, !busy);
        if (busy)
        {
            BusyLabel.Text = label ?? "Waiting…";
            ArmBusy();
        }
        else
            HideBusy();
    }

    private static void SetEnabled(Control control, bool enabled)
    {
        control.IsEnabled = enabled;
        control.Opacity = enabled ? AppTheme.Opacity.Opaque : AppTheme.Opacity.Strong;
    }

    private static double Font(string key) => (double)Application.Current.Resources[key];

    private static double Space(string key) => (double)Application.Current.Resources[key];

    private static Thickness Pad(string key) => (Thickness)Application.Current.Resources[key];

    private static CornerRadius Radius(string key) => (CornerRadius)Application.Current.Resources[key];

    private void ArmBusy()
    {
        _busyDelay ??= DispatcherQueue.CreateTimer();
        _busyDelay.Interval = TimeSpan.FromMilliseconds(200);
        _busyDelay.IsRepeating = false;
        _busyDelay.Tick -= OnBusyTick;
        _busyDelay.Tick += OnBusyTick;
        _busyDelay.Start();
    }

    private void OnBusyTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        if (!_busy)
            return;
        BusyOverlay.Visibility = Visibility.Visible;
        BusyRing.IsActive = true;
    }

    private void HideBusy()
    {
        _busyDelay?.Stop();
        BusyOverlay.Visibility = Visibility.Collapsed;
        BusyRing.IsActive = false;
    }
}
