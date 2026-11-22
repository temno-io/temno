using System.Net;
using Temno.Core.Account;
using Temno.Core.Agent;
using Temno.Core.Transcription;
using Temno.Environments;
using Temno.Generation;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// WP-260: every request to the Gateway (and later Billing) names the build's environment, so a
/// production service can refuse a development build. Supabase requests never carry it.
/// </summary>
public sealed class HostedEnvironmentHeaderTests
{
    private const string Gateway = "https://gateway.dev.test";
    private const string Supabase = "https://devref.supabase.co";

    private sealed class Recorder : HttpMessageHandler
    {
        public List<(string Url, string[]? Environment)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((
                request.RequestUri!.AbsoluteUri,
                request.Headers.TryGetValues(TemnoEnvironments.HeaderName, out var values) ? values.ToArray() : null));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("{\"error\":\"scripted\"}"),
            });
        }
    }

    private static async Task<(string Url, string[]? Environment)> OneRequest(Func<HttpClient, Task> call)
    {
        var recorder = new Recorder();
        try { await call(new HttpClient(recorder)); }
        catch (Exception) { /* A permanent refusal ends the stamp-only probe without transport retries. */ }
        return Assert.Single(recorder.Requests);
    }

    private static void AssertStamped((string Url, string[]? Environment) request, string path)
    {
        Assert.Equal(Gateway + path, request.Url);
        Assert.NotNull(request.Environment);
        Assert.Equal(new[] { HostedEndpoints.BuildEnvironment.Name() }, request.Environment);
    }

    private static void AssertNotStamped((string Url, string[]? Environment) request)
    {
        Assert.StartsWith(Supabase + "/", request.Url);
        Assert.Null(request.Environment);
    }

    [Fact]
    public void The_test_build_sends_development()
        => Assert.Equal("development", HostedEndpoints.BuildEnvironment.Name());

    [Fact]
    public async Task Agent_client_stamps_requests_and_the_event_stream()
    {
        AssertStamped(
            await OneRequest(http => new HostedAgentClient(Gateway, () => "token", http).GetCatalogAsync()),
            "/v1/agent/catalog");

        var turn = Guid.NewGuid();
        AssertStamped(
            await OneRequest(async http =>
            {
                await foreach (var _ in new HostedAgentClient(Gateway, () => "token", http).StreamEventsAsync(turn, 3)) { }
            }),
            "/v1/agent/turns/" + turn.ToString("D") + "/events");
    }

    [Fact]
    public async Task Transcription_client_stamps_requests()
        => AssertStamped(
            await OneRequest(http => new HostedTranscriptionClient(Gateway, () => "token", http).GetCapabilityAsync()),
            "/v1/transcription/capability");

    [Fact]
    public async Task Credit_bootstrap_is_stamped_and_the_supabase_balance_read_is_not()
    {
        AssertStamped(
            await OneRequest(http => new CreditBalanceClient(Supabase, "sb_publishable_dev", http).BootstrapAsync(Gateway, "token")),
            "/v1/account/bootstrap");
        AssertNotStamped(
            await OneRequest(http => new CreditBalanceClient(Supabase, "sb_publishable_dev", http).ReadAsync("token")));
    }

    [Fact]
    public async Task Media_image_and_generation_clients_stamp_requests()
    {
        AssertStamped(
            await OneRequest(http => new HostedMediaClient(Gateway, () => "token", http).GetCatalogAsync()),
            "/v1/media/catalog");
        AssertStamped(
            await OneRequest(http => new HostedImageClient(Gateway, () => "token", http).GetCatalogAsync()),
            "/v1/images/catalog");
        AssertStamped(
            await OneRequest(http => new HostedGenerationClient(Gateway, () => "token", http).GetJobStatusAsync("job-1")),
            "/v1/jobs/job-1");
    }

    [Fact]
    public async Task Supabase_auth_requests_are_never_stamped()
    {
        AssertNotStamped(await OneRequest(http => new SupabaseAuthClient(Supabase, "sb_publishable_dev", http).RefreshAsync("refresh")));
        AssertNotStamped(await OneRequest(http => new SupabaseAuthClient(Supabase, "sb_publishable_dev", http).ExchangeCodeAsync("code", "verifier")));
        AssertNotStamped(await OneRequest(http => new SupabaseAuthClient(Supabase, "sb_publishable_dev", http).ReadProfileAsync("token")));
        AssertNotStamped(await OneRequest(http => new SupabaseAuthClient(Supabase, "sb_publishable_dev", http).LogoutAsync("token")));
    }
}
