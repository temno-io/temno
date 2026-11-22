using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Temno.Core.Account;

public sealed record BillingPlanOffer(
    string TierKey,
    string DisplayName,
    long MonthlyCredits,
    long UnitAmount,
    string Currency,
    string? Interval)
{
    public string PriceLine()
    {
        var price = FormatPrice(UnitAmount, Currency);
        return string.Equals(Interval, "month", StringComparison.Ordinal) ? price + " a month" : price;
    }

    public string CreditsLine() => FormatCredits(MonthlyCredits) + " credits a month";

    public static string FormatCredits(long credits) => credits.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>
    /// Minor units to a price. Zero-decimal currencies are Stripe's list: dividing those by 100
    /// would show a yen price as a hundredth of itself.
    /// </summary>
    public static string FormatPrice(long unitAmount, string currency)
    {
        var code = currency.Trim().ToLowerInvariant();
        var zero = ZeroDecimal.Contains(code);
        var major = zero ? unitAmount : unitAmount / 100m;
        if (code == "usd")
            return major.ToString("C", CultureInfo.GetCultureInfo("en-US"));
        return zero
            ? string.Create(CultureInfo.InvariantCulture, $"{major:0} {code.ToUpperInvariant()}")
            : string.Create(CultureInfo.InvariantCulture, $"{major:0.00} {code.ToUpperInvariant()}");
    }

    private static readonly HashSet<string> ZeroDecimal = new(StringComparer.Ordinal)
    {
        "bif", "clp", "djf", "gnf", "jpy", "kmf", "krw", "mga", "pyg", "rwf", "ugx", "vnd", "vuv", "xaf", "xof", "xpf",
    };
}

/// <summary>
/// Credits sold in packs of <see cref="Credits"/> at <see cref="UnitAmount"/> each; a purchase is 1 to
/// <see cref="MaxQuantity"/> packs (owner, 2026-10-05). Prices come from the accounts host.
/// </summary>
public sealed record CreditPackOffer(long Credits, long UnitAmount, string Currency, int MaxQuantity = 1)
{
    public string ActionLabel(int quantity = 1) => "Buy " + BillingPlanOffer.FormatCredits(Credits * quantity) + " credits";

    /// <summary>One choice in the amount list: "3,000 credits · $30.00".</summary>
    public string AmountLabel(int quantity) =>
        BillingPlanOffer.FormatCredits(Credits * quantity) + " credits · "
        + BillingPlanOffer.FormatPrice(UnitAmount * quantity, Currency);

    public string PriceLine() =>
        BillingPlanOffer.FormatPrice(UnitAmount, Currency) + " per " + BillingPlanOffer.FormatCredits(Credits)
        + " credits. Credits expire 12 months after purchase.";

    /// <summary>
    /// The grant that shows this purchase arrived: newer than the newest top-up grant read before
    /// Checkout opened, and of the amount this purchase asked for. <paramref name="recent"/> is
    /// newest first. The balance cannot tell: spending can hide the grant and a plan renewal can
    /// imitate it. Another purchase's grant landing meanwhile, such as a delayed payment, does not
    /// end the wait unless it is the same amount, when the message is as true. Unknown before,
    /// nothing is ever claimed.
    /// </summary>
    public static TopUpGrantMark? ArrivedGrant(
        bool beforeKnown, TopUpGrantMark? before, IReadOnlyList<TopUpGrantMark> recent, long amount)
    {
        if (!beforeKnown)
            return null;
        foreach (var grant in recent)
        {
            if (before is not null && grant.Id == before.Id)
                return null;
            if (grant.Amount == amount)
                return grant;
        }
        return null;
    }

    /// <summary>WP-273 D-2: the server sells packs to an active or trialing plan only.</summary>
    public static bool IsEntitled(AccountSubscription? subscription) =>
        subscription?.Status is "active" or "trialing";
}

/// <summary>One of the caller's top-up grants, as their own credit_grant row shows it.</summary>
public sealed record TopUpGrantMark(Guid Id, long Amount, DateTimeOffset? CreatedAt);

public sealed record BillingCatalog(IReadOnlyList<BillingPlanOffer> Plans, CreditPackOffer? TopUp);

public readonly record struct CheckoutStart(string Url, bool Reused);

public sealed record AccountSubscription(
    string TierKey,
    string Status,
    bool CancelAtPeriodEnd,
    DateTimeOffset? PeriodEnd)
{
    public bool IsCurrent => Status is "active" or "trialing" or "past_due" or "incomplete";

    public bool HasCheckoutUpdate(long? totalBeforeCheckout, CreditBalance? balance) =>
        Status == "active" && totalBeforeCheckout is { } before && balance is { } current
        && current.Total > before;

    public string Summary(BillingPlanOffer? plan)
    {
        var name = string.IsNullOrWhiteSpace(plan?.DisplayName) ? TierKey : plan!.DisplayName;
        var credits = plan is null ? "" : " · " + plan.CreditsLine();
        if (CancelAtPeriodEnd)
        {
            var when = PeriodEnd is { } end ? " on " + end.ToLocalTime().ToString("D", CultureInfo.CurrentCulture) : "";
            return $"{name}{credits} · cancels at the end of the period{when}.";
        }

        return Status switch
        {
            "past_due" => $"{name}{credits} · payment past due. Manage billing to keep it.",
            "incomplete" => $"{name}{credits} · payment not finished. Manage billing to finish it.",
            _ => $"{name}{credits}.",
        };
    }
}

/// <summary>Sentences the Account panel shows. The panel does not invent a second wording.</summary>
public static class BillingSurfaceCopy
{
    public const string BillingNotActive =
        "Subscriptions are not on yet. The editor still works, and any credits you already have stay available.";

    public const string AlreadySubscribed =
        "You already have a plan. Manage billing to change or cancel it.";

    public const string NetworkError =
        "Plans could not be loaded. Check the connection and try again.";

    public const string ReusedCheckout =
        "This checkout was already open, so the same page was opened again.";

    public const string WaitingForGrant =
        "Waiting for plan credits. They show up here once Stripe confirms the payment.";

    public static string CheckoutUpdated(long available) =>
        $"Your plan is active. Available {BillingPlanOffer.FormatCredits(available)} credits.";

    public const string TopUpNeedsPlan =
        "Credit packs are for subscribers. Choose a plan to buy extra credits.";

    public const string TopUpNeedsActivePlan =
        "Credit packs need an active plan. Manage billing to bring yours up to date.";

    public const string WaitingForTopUp =
        "Waiting for the credits. They show up here once Stripe confirms the payment.";

    public static string TopUpArrived(long credits, long available) =>
        $"{BillingPlanOffer.FormatCredits(credits)} credits added. Available {BillingPlanOffer.FormatCredits(available)} credits.";

    public static string For(string? code) => (code ?? "").Trim().ToLowerInvariant() switch
    {
        "billing_not_active" => BillingNotActive,
        "already_subscribed" => AlreadySubscribed,
        "network_error" or "billing_unavailable" or "plans_unavailable" => NetworkError,
        "reused" => ReusedCheckout,
        "open_checkout_for_another_tier" =>
            "A checkout for another plan is still open. Finish it in the browser, or wait for it to expire.",
        "no_billing_customer" => "Choose a plan first. There is no billing account to manage yet.",
        "portal_not_configured" => "Billing management is not available right now.",
        "unknown_tier" => "That plan is not for sale.",
        "tier_unavailable" => "That plan is not available right now.",
        "checkout_unavailable" => "Checkout is not available right now. Try again in a few minutes.",
        "subscription_required" => TopUpNeedsPlan,
        "top_up_unavailable" => "Credit packs are not available right now.",
        "invalid_quantity" => "That amount of credits can't be bought in one purchase.",
        "unauthorized" => "Sign in again to manage billing.",
        "accounts_not_configured" => "Billing is not configured in this build.",
        "invalid_billing_url" => "The billing page address was not a Stripe link.",
        "stripe_account_guard_failed" => "Billing is not available right now.",
        _ => "Billing could not be reached. Try again.",
    };

    public static bool IsCalm(string? code) => code is "billing_not_active" or "accounts_not_configured"
        or "portal_not_configured" or "no_billing_customer" or "stripe_account_guard_failed"
        or "subscription_required" or "top_up_unavailable";

    public static bool IsNetwork(string? code) =>
        code is "network_error" or "billing_unavailable" or "plans_unavailable";
}

/// <summary>
/// Desktop caller for the accounts host and the signed-in subscription row. Prices come from
/// <see cref="ListPlansAsync"/>; this type holds no price table.
/// </summary>
public sealed class BillingClient
{
    private readonly HttpClient _http;

    public BillingClient(HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    public async Task<IReadOnlyList<BillingPlanOffer>> ListPlansAsync(
        string accountsUrl, string accessToken, CancellationToken cancellationToken = default) =>
        (await ListCatalogAsync(accountsUrl, accessToken, cancellationToken).ConfigureAwait(false)).Plans;

    /// <summary>The plans, and the credit pack when the host sells one. A malformed pack is left out.</summary>
    public async Task<BillingCatalog> ListCatalogAsync(
        string accountsUrl, string accessToken, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Service(accountsUrl, "/billing/plans"));
        Authorize(request, accessToken);
        var json = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (json?["plans"] is not JsonArray plans)
            throw new HostedApiException(BillingSurfaceCopy.For("plans_unavailable"), code: "plans_unavailable");

        var list = new List<BillingPlanOffer>(plans.Count);
        foreach (var node in plans)
        {
            if (node is not JsonObject row
                || Text(row, "tier_key") is not { } key
                || Text(row, "display_name") is not { } name
                || Number(row, "monthly_credits") is not long credits
                || Number(row, "unit_amount") is not long amount
                || Text(row, "currency") is not { } currency)
                throw new HostedApiException(BillingSurfaceCopy.For("plans_unavailable"), code: "plans_unavailable");
            list.Add(new BillingPlanOffer(key, name, credits, amount, currency, Text(row, "interval")));
        }

        CreditPackOffer? topUp = null;
        if (json?["top_up"] is JsonObject pack
            && Number(pack, "credits") is long packCredits and > 0
            && Number(pack, "unit_amount") is long packAmount and > 0
            && Text(pack, "currency") is { } packCurrency)
        {
            // An accounts host from before the adjustable amount sells one pack at a time.
            var maxQuantity = Number(pack, "max_quantity") is long max and >= 1 and <= 100 ? (int)max : 1;
            topUp = new CreditPackOffer(packCredits, packAmount, packCurrency, maxQuantity);
        }

        return new BillingCatalog(list, topUp);
    }

    /// <summary>
    /// Opens a checkout for <paramref name="quantity"/> packs. The host decides the pack, its price,
    /// the allowed count and who may buy it.
    /// </summary>
    public async Task<CheckoutStart> StartTopUpAsync(
        string accountsUrl, string accessToken, int quantity = 1, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(quantity, 1);
        using var request = new HttpRequestMessage(HttpMethod.Post, Service(accountsUrl, "/billing/top-up"))
        {
            Content = new StringContent(
                "{\"quantity\":" + quantity.ToString(CultureInfo.InvariantCulture) + "}", Encoding.UTF8, "application/json"),
        };
        Authorize(request, accessToken);
        var json = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        var url = json?["checkout_url"] is JsonValue value ? value.GetValue<string>() : null;
        if (!IsStripeUrl(url))
            throw new HostedApiException(BillingSurfaceCopy.For("invalid_billing_url"), code: "invalid_billing_url");
        var reused = json?["reused"] is JsonValue flag && flag.TryGetValue<bool>(out var parsed) && parsed;
        return new CheckoutStart(url!, reused);
    }

    public async Task<CheckoutStart> StartCheckoutAsync(
        string accountsUrl, string accessToken, string tierKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tierKey);
        using var request = new HttpRequestMessage(HttpMethod.Post, Service(accountsUrl, "/billing/checkout"))
        {
            Content = new StringContent("{\"tier_key\":" + JsonString(tierKey) + "}", Encoding.UTF8, "application/json"),
        };
        Authorize(request, accessToken);
        var json = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        var url = json?["checkout_url"] is JsonValue value ? value.GetValue<string>() : null;
        if (!IsStripeUrl(url))
            throw new HostedApiException(BillingSurfaceCopy.For("invalid_billing_url"), code: "invalid_billing_url");
        var reused = json?["reused"] is JsonValue flag && flag.TryGetValue<bool>(out var parsed) && parsed;
        return new CheckoutStart(url!, reused);
    }

    public async Task<string> StartPortalAsync(
        string accountsUrl, string accessToken, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Service(accountsUrl, "/billing/portal"))
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        Authorize(request, accessToken);
        var json = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        var url = json?["portal_url"] is JsonValue value ? value.GetValue<string>() : null;
        if (!IsStripeUrl(url))
            throw new HostedApiException(BillingSurfaceCopy.For("invalid_billing_url"), code: "invalid_billing_url");
        return url!;
    }

    public async Task<AccountSubscription?> ReadSubscriptionAsync(
        string supabaseUrl, string anonKey, string accessToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(anonKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        if (!HostedEndpoints.IsServiceUrl(supabaseUrl))
            throw new ArgumentException("The account project must use HTTPS or a local test endpoint.", nameof(supabaseUrl));

        var filter = Uri.EscapeDataString("in.(incomplete,trialing,active,past_due)");
        var url = supabaseUrl.TrimEnd('/')
            + "/rest/v1/credit_subscription?select=tier_key,status,cancel_at_period_end,current_period_end&status="
            + filter;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("apikey", anonKey);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var json = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (json is not JsonArray rows || rows.Count == 0)
            return null;

        AccountSubscription? best = null;
        var bestRank = int.MaxValue;
        foreach (var node in rows)
        {
            if (node is not JsonObject row || Text(row, "tier_key") is not { } key || Text(row, "status") is not { } status)
                continue;
            var rank = Rank(status);
            if (rank >= bestRank)
                continue;
            bestRank = rank;
            best = new AccountSubscription(key, status, Flag(row, "cancel_at_period_end"), When(row, "current_period_end"));
        }

        return best;
    }

    /// <summary>
    /// The newest <see cref="RecentTopUpGrants"/> <c>stripe_checkout</c> grants on the caller's own
    /// lots, newest first (RLS: own rows; the grant key, which holds the Stripe session id, is not
    /// readable). Empty when there are none.
    /// </summary>
    public async Task<IReadOnlyList<TopUpGrantMark>> ReadRecentTopUpGrantsAsync(
        string supabaseUrl, string anonKey, string accessToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(anonKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        if (!HostedEndpoints.IsServiceUrl(supabaseUrl))
            throw new ArgumentException("The account project must use HTTPS or a local test endpoint.", nameof(supabaseUrl));

        var url = supabaseUrl.TrimEnd('/')
            + "/rest/v1/credit_grant?select=id,amount,created_at&source=eq.stripe_checkout"
            + "&order=created_at.desc,id.desc&limit=" + RecentTopUpGrants;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("apikey", anonKey);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var json = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        var grants = new List<TopUpGrantMark>();
        if (json is JsonArray rows)
        {
            foreach (var node in rows)
            {
                if (node is JsonObject row && Guid.TryParse(Text(row, "id"), out var id)
                    && Number(row, "amount") is { } amount)
                    grants.Add(new TopUpGrantMark(id, amount, When(row, "created_at")));
            }
        }
        return grants;
    }

    /// <summary>How many recent top-up grants the Account panel reads to confirm a purchase.</summary>
    public const int RecentTopUpGrants = 5;

    /// <summary>Checkout and portal URLs must be https on Stripe. Anything else is not opened.</summary>
    public static bool IsStripeUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) || uri.UserInfo.Length != 0)
            return false;
        var host = uri.IdnHost;
        return host.Equals("stripe.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".stripe.com", StringComparison.OrdinalIgnoreCase);
    }

    private static int Rank(string status) => status switch
    {
        "active" => 0,
        "trialing" => 1,
        "past_due" => 2,
        "incomplete" => 3,
        _ => 9,
    };

    private async Task<JsonNode?> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new HostedApiException(BillingSurfaceCopy.NetworkError, code: "network_error");
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            JsonNode? json = null;
            if (!string.IsNullOrWhiteSpace(body))
            {
                try
                {
                    json = JsonNode.Parse(body);
                }
                catch (System.Text.Json.JsonException)
                {
                    json = null;
                }
            }

            if (!response.IsSuccessStatusCode)
            {
                HostedApiErrors.TryParse(body, out var code, out _);
                code ??= ((int)response.StatusCode) switch
                {
                    401 => "unauthorized",
                    409 => "already_subscribed",
                    503 => "billing_unavailable",
                    _ => "billing_unavailable",
                };
                throw new HostedApiException(BillingSurfaceCopy.For(code), (int)response.StatusCode, code);
            }

            return json;
        }
    }

    private static Uri Service(string accountsUrl, string path)
    {
        if (!HostedEndpoints.IsServiceUrl(accountsUrl))
            throw new ArgumentException("The accounts host must use HTTPS or a local test endpoint.", nameof(accountsUrl));
        return new Uri(accountsUrl.TrimEnd('/') + path);
    }

    private static void Authorize(HttpRequestMessage request, string accessToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        HostedEndpoints.StampEnvironment(request);
    }

    private static string JsonString(string value) => System.Text.Json.JsonSerializer.Serialize(value);

    private static string? Text(JsonObject row, string name) =>
        row[name] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;

    private static long? Number(JsonObject row, string name)
    {
        if (row[name] is not JsonValue value)
            return null;
        if (value.TryGetValue(out long number))
            return number;
        if (value.TryGetValue(out int smaller))
            return smaller;
        return null;
    }

    private static bool Flag(JsonObject row, string name) =>
        row[name] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    private static DateTimeOffset? When(JsonObject row, string name) =>
        row[name] is JsonValue value && value.TryGetValue<string>(out var text)
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when)
            ? when
            : null;
}
