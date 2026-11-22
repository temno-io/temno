using System.Net;
using System.Text;
using Temno.Core.Account;
using Xunit;

namespace Temno.Core.Tests;

public sealed class BillingClientTests
{
    [Fact]
    public async Task Plans_parse_price_credits_and_ignore_a_price_id()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue("""
            {"plans":[
              {"tier_key":"max","display_name":"Max","monthly_credits":8000,"unit_amount":6900,"currency":"usd","interval":"month","price_id":"price_secret"},
              {"tier_key":"pro","display_name":"Pro","monthly_credits":3000,"unit_amount":2900,"currency":"usd","interval":"month"}
            ]}
            """);
        using var http = new HttpClient(handler);
        var client = new BillingClient(http);

        var plans = await client.ListPlansAsync("https://accounts.example.test", "token");

        Assert.Equal(2, plans.Count);
        Assert.Equal("Max", plans[0].DisplayName);
        Assert.Equal("$69.00 a month", plans[0].PriceLine());
        Assert.Equal("8,000 credits a month", plans[0].CreditsLine());
        Assert.Equal("$29.00 a month", plans[1].PriceLine());
        Assert.Contains("/billing/plans", handler.Urls[0], StringComparison.Ordinal);
        Assert.Equal("development", handler.Header(0, "X-Temno-Environment"));
    }

    [Fact]
    public async Task Checkout_reports_a_reused_session_and_sends_only_the_tier_key()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue("""{"checkout_url":"https://checkout.stripe.com/c/pay/cs_test","session_id":"cs_test","reused":true}""");
        using var http = new HttpClient(handler);
        var client = new BillingClient(http);

        var start = await client.StartCheckoutAsync("https://accounts.example.test", "token", "pro");

        Assert.True(start.Reused);
        Assert.Equal("https://checkout.stripe.com/c/pay/cs_test", start.Url);
        Assert.Equal("{\"tier_key\":\"pro\"}", handler.Bodies[0]);
    }

    [Fact]
    public async Task Already_subscribed_billing_off_and_network_keep_their_codes()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue("""{"error":"already_subscribed","status":"active"}""", HttpStatusCode.Conflict);
        handler.Enqueue("""{"error":"billing_not_active"}""", HttpStatusCode.ServiceUnavailable);
        handler.EnqueueThrow(new HttpRequestException("down"));
        using var http = new HttpClient(handler);
        var client = new BillingClient(http);

        var subscribed = await Assert.ThrowsAsync<HostedApiException>(() =>
            client.StartCheckoutAsync("https://accounts.example.test", "token", "pro"));
        var inactive = await Assert.ThrowsAsync<HostedApiException>(() =>
            client.ListPlansAsync("https://accounts.example.test", "token"));
        var network = await Assert.ThrowsAsync<HostedApiException>(() =>
            client.ListPlansAsync("https://accounts.example.test", "token"));

        Assert.Equal("already_subscribed", subscribed.Code);
        Assert.Equal(BillingSurfaceCopy.AlreadySubscribed, subscribed.Message);
        Assert.Equal("billing_not_active", inactive.Code);
        Assert.Equal(BillingSurfaceCopy.BillingNotActive, inactive.Message);
        Assert.Equal("network_error", network.Code);
        Assert.Equal(BillingSurfaceCopy.NetworkError, network.Message);
    }

    [Fact]
    public async Task A_non_stripe_checkout_url_is_refused()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue("""{"checkout_url":"https://evil.example/pay","reused":false}""");
        using var http = new HttpClient(handler);
        var client = new BillingClient(http);

        var ex = await Assert.ThrowsAsync<HostedApiException>(() =>
            client.StartCheckoutAsync("https://accounts.example.test", "token", "pro"));

        Assert.Equal("invalid_billing_url", ex.Code);
        Assert.False(BillingClient.IsStripeUrl("http://checkout.stripe.com/pay"));
        Assert.True(BillingClient.IsStripeUrl("https://billing.stripe.com/p/session/test"));
    }

    [Fact]
    public async Task Portal_returns_the_stripe_url()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue("""{"portal_url":"https://billing.stripe.com/p/session/cus_test"}""");
        using var http = new HttpClient(handler);
        var client = new BillingClient(http);

        var url = await client.StartPortalAsync("http://127.0.0.1:5241", "token");

        Assert.Equal("https://billing.stripe.com/p/session/cus_test", url);
    }

    [Fact]
    public async Task Subscription_prefers_the_live_row_and_reads_cancel_at_period_end()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue("""
            [
              {"tier_key":"pro","status":"incomplete","cancel_at_period_end":false,"current_period_end":null},
              {"tier_key":"max","status":"active","cancel_at_period_end":true,"current_period_end":"2026-10-29T00:00:00+00:00"}
            ]
            """);
        using var http = new HttpClient(handler);
        var client = new BillingClient(http);

        var subscription = await client.ReadSubscriptionAsync("https://project.example.test", "anon", "token");

        Assert.NotNull(subscription);
        Assert.Equal("max", subscription!.TierKey);
        Assert.True(subscription.CancelAtPeriodEnd);
        Assert.True(subscription.IsCurrent);
        Assert.Contains("cancels at the end of the period", subscription.Summary(new BillingPlanOffer(
            "max", "Max", 8000, 6900, "usd", "month")), StringComparison.Ordinal);
        Assert.Contains("credit_subscription", handler.Urls[0], StringComparison.Ordinal);
        Assert.Null(handler.Header(0, "X-Temno-Environment"));
    }

    [Fact]
    public void Yen_is_not_divided_by_one_hundred()
    {
        Assert.Equal("2900 JPY", BillingPlanOffer.FormatPrice(2900, "jpy"));
        Assert.Equal("$29.00", BillingPlanOffer.FormatPrice(2900, "usd"));
    }

    [Fact]
    public void Checkout_does_not_treat_an_unknown_balance_as_zero()
    {
        var active = new AccountSubscription("pro", "active", false, null);
        Assert.False(active.HasCheckoutUpdate(null, new CreditBalance(250, 0, 250)));
        Assert.False(active.HasCheckoutUpdate(250, null));
    }

    [Fact]
    public void Released_credit_reservations_do_not_complete_checkout()
    {
        var active = new AccountSubscription("pro", "active", false, null);
        Assert.False(active.HasCheckoutUpdate(250, new CreditBalance(250, 0, 250)));
        Assert.True(active.HasCheckoutUpdate(250, new CreditBalance(3250, 100, 3150)));
        Assert.Equal("Your plan is active. Available 3,150 credits.", BillingSurfaceCopy.CheckoutUpdated(3150));
    }

    [Theory]
    [InlineData("incomplete")]
    [InlineData("trialing")]
    [InlineData("past_due")]
    [InlineData("canceled")]
    public void Unpaid_or_inactive_subscription_does_not_confirm_checkout(string status)
    {
        var subscription = new AccountSubscription("pro", status, false, null);
        Assert.False(subscription.HasCheckoutUpdate(250, new CreditBalance(3250, 0, 3250)));
    }

    [Fact]
    public async Task The_catalog_carries_the_credit_pack_and_leaves_out_a_malformed_one()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue("""
            {"plans":[{"tier_key":"pro","display_name":"Pro","monthly_credits":3000,"unit_amount":2900,"currency":"usd","interval":"month"}],
             "top_up":{"credits":1000,"unit_amount":1000,"currency":"usd","max_quantity":10,"price_id":"price_secret"}}
            """);
        handler.Enqueue("""{"plans":[],"top_up":null}""");
        handler.Enqueue("""{"plans":[],"top_up":{"credits":0,"unit_amount":1000,"currency":"usd"}}""");
        handler.Enqueue("""{"plans":[]}""");
        handler.Enqueue("""{"plans":[],"top_up":{"credits":1000,"unit_amount":1000,"currency":"usd"}}""");
        handler.Enqueue("""{"plans":[],"top_up":{"credits":1000,"unit_amount":1000,"currency":"usd","max_quantity":1000}}""");
        using var http = new HttpClient(handler);
        var client = new BillingClient(http);

        var offered = await client.ListCatalogAsync("https://accounts.example.test", "token");
        var none = await client.ListCatalogAsync("https://accounts.example.test", "token");
        var malformed = await client.ListCatalogAsync("https://accounts.example.test", "token");
        var older = await client.ListCatalogAsync("https://accounts.example.test", "token");
        var onePackHost = await client.ListCatalogAsync("https://accounts.example.test", "token");
        var outOfRange = await client.ListCatalogAsync("https://accounts.example.test", "token");

        Assert.Single(offered.Plans);
        Assert.Equal(new CreditPackOffer(1000, 1000, "usd", 10), offered.TopUp);
        Assert.Equal("$10.00 per 1,000 credits. Credits expire 12 months after purchase.", offered.TopUp!.PriceLine());
        Assert.Equal("Buy 1,000 credits", offered.TopUp.ActionLabel());
        Assert.Equal("Buy 3,000 credits", offered.TopUp.ActionLabel(3));
        Assert.Equal("1,000 credits · $10.00", offered.TopUp.AmountLabel(1));
        Assert.Equal("10,000 credits · $100.00", offered.TopUp.AmountLabel(10));
        Assert.Null(none.TopUp);
        Assert.Null(malformed.TopUp);
        Assert.Null(older.TopUp);
        // A host from before the adjustable amount, or a count past the database's bound: one pack.
        Assert.Equal(1, onePackHost.TopUp!.MaxQuantity);
        Assert.Equal(1, outOfRange.TopUp!.MaxQuantity);
    }

    [Fact]
    public async Task A_top_up_posts_only_the_pack_count_and_opens_only_stripe()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue("""{"checkout_url":"https://checkout.stripe.com/c/pay/cs_test_topup","session_id":"cs_test_topup","reused":false}""");
        handler.Enqueue("""{"checkout_url":"https://evil.example/pay","reused":false}""");
        using var http = new HttpClient(handler);
        var client = new BillingClient(http);

        var start = await client.StartTopUpAsync("https://accounts.example.test", "token", 3);
        var refused = await Assert.ThrowsAsync<HostedApiException>(() =>
            client.StartTopUpAsync("https://accounts.example.test", "token"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            client.StartTopUpAsync("https://accounts.example.test", "token", 0));

        Assert.Equal("https://checkout.stripe.com/c/pay/cs_test_topup", start.Url);
        Assert.False(start.Reused);
        Assert.EndsWith("/billing/top-up", handler.Urls[0], StringComparison.Ordinal);
        Assert.Equal("{\"quantity\":3}", handler.Bodies[0]);
        Assert.Equal("{\"quantity\":1}", handler.Bodies[1]);
        Assert.Equal("development", handler.Header(0, "X-Temno-Environment"));
        Assert.Equal("invalid_billing_url", refused.Code);
        Assert.Equal(2, handler.Urls.Count);
    }

    [Fact]
    public async Task A_non_subscriber_and_an_unsold_pack_get_calm_explanations()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue("""{"error":"subscription_required"}""", HttpStatusCode.Forbidden);
        handler.Enqueue("""{"error":"top_up_unavailable"}""", HttpStatusCode.ServiceUnavailable);
        using var http = new HttpClient(handler);
        var client = new BillingClient(http);

        var required = await Assert.ThrowsAsync<HostedApiException>(() =>
            client.StartTopUpAsync("https://accounts.example.test", "token"));
        var unavailable = await Assert.ThrowsAsync<HostedApiException>(() =>
            client.StartTopUpAsync("https://accounts.example.test", "token"));

        Assert.Equal("subscription_required", required.Code);
        Assert.Equal(403, required.StatusCode);
        Assert.Equal(BillingSurfaceCopy.TopUpNeedsPlan, required.Message);
        Assert.Equal("top_up_unavailable", unavailable.Code);
        Assert.Equal("Credit packs are not available right now.", unavailable.Message);
        Assert.True(BillingSurfaceCopy.IsCalm("subscription_required"));
        Assert.False(BillingSurfaceCopy.IsNetwork("subscription_required"));
    }

    [Theory]
    [InlineData("active", true)]
    [InlineData("trialing", true)]
    [InlineData("past_due", false)]
    [InlineData("incomplete", false)]
    [InlineData("canceled", false)]
    public void Only_an_active_or_trialing_plan_may_buy_a_pack(string status, bool entitled)
    {
        Assert.Equal(entitled, CreditPackOffer.IsEntitled(new AccountSubscription("pro", status, false, null)));
        Assert.False(CreditPackOffer.IsEntitled(null));
    }

    [Fact]
    public void A_pack_shows_as_arrived_only_when_a_newer_top_up_grant_of_its_amount_exists()
    {
        var earlier = new TopUpGrantMark(Guid.NewGuid(), 1000, DateTimeOffset.Parse("2026-10-01T00:00:00Z"));
        var bought = new TopUpGrantMark(Guid.NewGuid(), 1000, DateTimeOffset.Parse("2026-10-05T10:00:00Z"));

        Assert.Equal(bought, CreditPackOffer.ArrivedGrant(true, null, [bought], 1000));
        Assert.Equal(bought, CreditPackOffer.ArrivedGrant(true, earlier, [bought, earlier], 1000));
        // A plan renewal during an abandoned Checkout adds credits but no top-up grant (WP-273 T2 F1).
        Assert.Null(CreditPackOffer.ArrivedGrant(true, earlier, [earlier], 1000));
        Assert.Null(CreditPackOffer.ArrivedGrant(true, null, [], 1000));
        // Before unknown (its read failed): never claim, however the balance moved.
        Assert.Null(CreditPackOffer.ArrivedGrant(false, null, [bought], 1000));
        Assert.Equal("1,000 credits added. Available 1,700 credits.", BillingSurfaceCopy.TopUpArrived(1000, 1700));
    }

    [Fact]
    public void Another_purchases_grant_does_not_end_the_wait_for_this_one()
    {
        // WP-273 T2 round 2, R2-F1: a delayed one-pack purchase lands while a three-pack is open.
        var earlier = new TopUpGrantMark(Guid.NewGuid(), 1000, DateTimeOffset.Parse("2026-10-01T00:00:00Z"));
        var delayed = new TopUpGrantMark(Guid.NewGuid(), 1000, DateTimeOffset.Parse("2026-10-05T10:00:00Z"));
        var threePack = new TopUpGrantMark(Guid.NewGuid(), 3000, DateTimeOffset.Parse("2026-10-05T10:01:00Z"));

        Assert.Null(CreditPackOffer.ArrivedGrant(true, earlier, [delayed, earlier], 3000));
        Assert.Equal(threePack, CreditPackOffer.ArrivedGrant(true, earlier, [threePack, delayed, earlier], 3000));
        // Either order: the three-pack landing first and the delayed one after still confirms it.
        Assert.Equal(threePack, CreditPackOffer.ArrivedGrant(true, earlier, [delayed, threePack, earlier], 3000));
        // A grant of this amount from before Checkout opened never counts.
        Assert.Null(CreditPackOffer.ArrivedGrant(true, delayed, [delayed, threePack], 3000));
    }

    [Fact]
    public async Task Recent_top_up_grants_read_the_callers_own_newest_checkout_lots()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue("""
            [{"id":"5f0c3b1e-8d2a-4c47-9a51-2e0d6f7b9c11","amount":3000,"created_at":"2026-10-05T10:00:00+00:00"},
             {"id":"not-a-guid","amount":1000},
             {"id":"0b6f0b8e-2b44-4f55-8a3d-61c1e2b3d4f5","amount":1000,"created_at":"2026-10-01T00:00:00+00:00"}]
            """);
        handler.Enqueue("[]");
        using var http = new HttpClient(handler);
        var client = new BillingClient(http);

        var marks = await client.ReadRecentTopUpGrantsAsync("https://project.example.test", "anon", "token");
        var none = await client.ReadRecentTopUpGrantsAsync("https://project.example.test", "anon", "token");

        Assert.Equal(
            [(Guid.Parse("5f0c3b1e-8d2a-4c47-9a51-2e0d6f7b9c11"), 3000L), (Guid.Parse("0b6f0b8e-2b44-4f55-8a3d-61c1e2b3d4f5"), 1000L)],
            marks.Select(m => (m.Id, m.Amount)));
        Assert.Empty(none);
        Assert.Equal(
            "https://project.example.test/rest/v1/credit_grant?select=id,amount,created_at&source=eq.stripe_checkout&order=created_at.desc,id.desc&limit=5",
            handler.Urls[0]);
        Assert.Equal("anon", handler.Header(0, "apikey"));
        Assert.Null(handler.Header(0, "X-Temno-Environment"));
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _queue = new();
        public List<string> Urls { get; } = [];
        public List<string> Bodies { get; } = [];
        private readonly List<HttpRequestMessage> _requests = [];

        public void Enqueue(string json, HttpStatusCode status = HttpStatusCode.OK)
        {
            _queue.Enqueue(_ => new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }

        public void EnqueueThrow(Exception exception) => _queue.Enqueue(_ => throw exception);

        public string? Header(int index, string name) =>
            _requests[index].Headers.TryGetValues(name, out var values) ? values.Single() : null;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Urls.Add(request.RequestUri?.ToString() ?? "");
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            _requests.Add(request);
            if (_queue.Count == 0)
                throw new InvalidOperationException("No scripted HTTP response remaining.");
            return _queue.Dequeue()(request);
        }
    }
}
