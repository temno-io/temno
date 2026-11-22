using Temno.Core.Account;
using Temno.Core.Generation;
using Temno.Environments;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>WP-350: where the app finds the events Worker, and how push changes the job watcher's cadence.</summary>
[Collection(nameof(HostedJobPushCollection))]
public sealed class AccountEventsWiringTests
{
    private static readonly EnvironmentIdentity Prod = new(
        "prodtestref00000000", "sb_publishable_prodtest", "gateway.prod.test", "accounts.prod.test", "acct_prodtest", "accounts@prod.test");

    private static string File(string? events) => $$"""
        {
          "environment": "development",
          "supabaseUrl": "https://devref.supabase.co",
          "supabaseAnonKey": "sb_publishable_devfile",
          "gatewayUrl": "https://gateway.dev.test",
          "accountsUrl": "https://accounts.dev.test"{{(events is null ? "" : $",\n  \"eventsUrl\": \"{events}\"")}}
        }
        """;

    private static HostedBackend Dev(string? fileEvents, string? envEvents = null)
        => HostedEndpoints.Resolve(TemnoEnvironment.Development, Prod,
            name => name == "TEMNO_EVENTS_URL" ? envEvents : null, () => File(fileEvents));

    [Fact]
    public void The_events_URL_is_optional_and_its_absence_is_no_problem()
    {
        var backend = Dev(null);
        Assert.False(backend.EventsConfigured);
        Assert.Equal("", backend.EventsUrl);
        Assert.Null(backend.Problem);
        Assert.True(backend.GatewayConfigured && backend.AccountsConfigured && backend.AuthConfigured);
    }

    [Fact]
    public void The_developer_file_or_a_variable_sets_it_and_the_variable_wins()
    {
        Assert.Equal("https://events.dev.test", Dev("https://events.dev.test/").EventsUrl);
        Assert.Equal("https://other.dev.test", Dev("https://events.dev.test", "https://other.dev.test").EventsUrl);
        Assert.Equal("http://127.0.0.1:8787", Dev("http://127.0.0.1:8787").EventsUrl);
    }

    [Theory]
    [InlineData("http://events.dev.test")]
    [InlineData("https://events.dev.test/?x=1")]
    [InlineData("https://gateway.prod.test")]
    public void A_bad_or_production_events_URL_turns_hosted_features_off(string url)
    {
        var backend = Dev(url);
        Assert.False(backend.AuthConfigured);
        Assert.False(backend.EventsConfigured);
        Assert.Contains("eventsUrl", backend.Problem);
    }

    [Fact]
    public void A_production_identity_without_events_keeps_polling()
    {
        var backend = HostedEndpoints.Resolve(TemnoEnvironment.Production, Prod, _ => "https://events.dev.test", () => File("https://events.dev.test"));
        Assert.False(backend.EventsConfigured);
    }

    [Fact]
    public void Production_events_come_only_from_the_compiled_identity()
    {
        var production = Prod with { EventsHost = "events.prod.test" };
        var backend = HostedEndpoints.Resolve(TemnoEnvironment.Production, production,
            _ => throw new InvalidOperationException("Production must not read environment overrides"),
            () => throw new InvalidOperationException("Production must not read developer configuration"));

        Assert.True(backend.EventsConfigured);
        Assert.Equal("https://events.prod.test", backend.EventsUrl);
    }

    [Theory]
    [InlineData("https://events.prod.test")]
    [InlineData("https://EVENTS.PROD.TEST:443/")]
    [InlineData("https://events.prod.test./")]
    public void Development_refuses_the_production_events_host(string eventsUrl)
    {
        var production = Prod with { EventsHost = "events.prod.test" };
        var backend = HostedEndpoints.Resolve(TemnoEnvironment.Development, production,
            _ => null, () => File(eventsUrl));

        Assert.False(backend.AuthConfigured);
        Assert.False(backend.EventsConfigured);
        Assert.Contains("points at production", backend.Problem);
    }

    [Fact]
    public void The_shipped_events_origin_is_a_production_host()
    {
        var production = EnvironmentIdentity.Production;
        Assert.Equal("https://gateway.temno.io", production.EventsUrl);
        Assert.True(production.NamesHostOf(production.EventsUrl));
        Assert.True(production.CoversHost("*.temno.io"));
        Assert.False(production.CoversHost("*.example.workers.dev"));
        Assert.False(production.NamesHostOf("https://events.example.workers.dev"));
    }

    [Fact]
    public void An_events_URL_that_is_not_text_is_refused_like_the_others()
    {
        Assert.False(DevelopmentBackendFile.TryParse("""{"environment":"development","eventsUrl":5}""", out _, out var problem));
        Assert.Contains("eventsUrl", problem);
        Assert.True(DevelopmentBackendFile.TryParse("""{"environment":"development","eventsUrl":"https://e.dev.test"}""", out var values, out _));
        Assert.Equal("https://e.dev.test", values.EventsUrl);
    }

    [Fact]
    public void Live_push_slows_the_watchers_own_reads_to_a_minute_and_honours_a_longer_retry_after()
    {
        var random = new Random(3);
        try
        {
            HostedJobPush.Live = false;
            Assert.InRange(HostedJobPush.Gap(random, null).TotalSeconds, 2, 10);
            HostedJobPush.Live = true;
            Assert.Equal(TimeSpan.FromSeconds(60), HostedJobPush.Gap(random, null));
            Assert.Equal(TimeSpan.FromSeconds(60), HostedJobPush.Gap(random, TimeSpan.FromSeconds(5)));
            Assert.Equal(TimeSpan.FromSeconds(90), HostedJobPush.Gap(random, TimeSpan.FromSeconds(90)));
        }
        finally
        {
            HostedJobPush.Live = false;
        }
    }

    [Fact]
    public async Task A_job_tickle_wakes_every_watcher_and_losing_push_wakes_them_too()
    {
        var root = Path.Combine(Path.GetTempPath(), "wp350-" + Guid.NewGuid().ToString("N"));
        var store = new HostedPendingStore(Path.Combine(root, "pending.json"));
        var registry = HostedWatcherRegistry.Shared;
        var waits = 0;
        var woken = new TaskCompletionSource();
        async Task Delay(TimeSpan gap, CancellationToken ct)
        {
            if (Interlocked.Increment(ref waits) > 1)
            {
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { woken.TrySetResult(); throw; }
            }
        }

        var account = "wp350-" + Guid.NewGuid().ToString("N");
        var watcher = registry.Ensure(account, "development",
            () => new HostedJobWatcher(store, new IdleTransport(), "development", account, Delay));
        try
        {
            await WaitFor(() => Volatile.Read(ref waits) >= 2);
            registry.WakeAll();
            await woken.Task.WaitAsync(TimeSpan.FromSeconds(5));

            woken = new TaskCompletionSource();
            await WaitFor(() => Volatile.Read(ref waits) >= 3);
            HostedJobPush.Live = true;
            HostedJobPush.Live = false;
            await woken.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            watcher.Stop();
            HostedJobPush.Live = false;
        }
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException();
            await Task.Delay(5);
        }
    }

    private sealed class IdleTransport : IHostedJobTransport
    {
        public Task<HostedJobObservation> AdvanceAsync(HostedPendingJob job, CancellationToken cancellationToken)
            => Task.FromResult(HostedJobObservation.Working(HostedJobPhases.Pending, job.JobId));
    }
}

[CollectionDefinition(nameof(HostedJobPushCollection), DisableParallelization = true)]
public sealed class HostedJobPushCollection;
