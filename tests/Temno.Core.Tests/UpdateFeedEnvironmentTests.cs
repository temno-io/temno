using System.Text.Json;
using Temno.Core.Account;
using Temno.Core.IO;
using Temno.Core.Updates;
using Temno.Environments;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// WP-375: a build carries its update feed beside its other hosts. Production's is compiled in (WP-376:
/// download.temno.io); development's is the developer file's <c>updateFeedUrl</c>. Synthetic identities, no real file.
/// </summary>
[Collection("EnvRegistry")]
public sealed class UpdateFeedEnvironmentTests : IDisposable
{
    private const string DevFeed = "https://download.dev.test/windows/stable.json";

    private static readonly EnvironmentIdentity Prod = new(
        "prodtestref00000000", "sb_publishable_prodtest", "gateway.prod.test", "accounts.prod.test", "acct_prodtest",
        "accounts@prod.test", UpdateHost: "download.prod.test");

    private readonly string? _previousFeedVariable = Environment.GetEnvironmentVariable("TEMNO_UPDATE_FEED_URL");

    public UpdateFeedEnvironmentTests() => Environment.SetEnvironmentVariable("TEMNO_UPDATE_FEED_URL", null);

    public void Dispose() => Environment.SetEnvironmentVariable("TEMNO_UPDATE_FEED_URL", _previousFeedVariable);

    private static string File(string? feed, bool backend = true)
    {
        var members = new List<string> { "\"environment\": \"development\"" };
        if (backend)
        {
            members.Add("\"supabaseUrl\": \"https://devref.supabase.co\"");
            members.Add("\"supabaseAnonKey\": \"sb_publishable_devfile\"");
            members.Add("\"gatewayUrl\": \"https://gateway.dev.test\"");
            members.Add("\"accountsUrl\": \"https://accounts.dev.test\"");
        }
        if (feed is not null)
            members.Add("\"updateFeedUrl\": " + JsonSerializer.Serialize(feed));
        return "{\n  " + string.Join(",\n  ", members) + "\n}";
    }

    private static HostedBackend Dev(string? feed, bool backend = true, Func<string, string?>? env = null)
        => HostedEndpoints.Resolve(TemnoEnvironment.Development, Prod, env ?? (_ => null), () => File(feed, backend));

    [Fact]
    public void Production_reads_its_feed_from_the_compiled_identity_only()
    {
        var backend = HostedEndpoints.Resolve(TemnoEnvironment.Production, Prod,
            _ => throw new InvalidOperationException("Production must not read environment overrides"),
            () => throw new InvalidOperationException("Production must not read developer configuration"));

        Assert.Equal("https://download.prod.test/windows/stable.json", backend.UpdateFeedUrl);
        Assert.Equal(Prod.UpdateFeedUrl, backend.UpdateFeedUrl);
    }

    [Fact]
    public void Production_without_an_update_host_has_no_feed()
    {
        var backend = HostedEndpoints.Resolve(TemnoEnvironment.Production, Prod with { UpdateHost = "" }, _ => DevFeed, () => File(DevFeed));
        Assert.Equal("", backend.UpdateFeedUrl);

        var hostless = new EnvironmentIdentity("", "", "", "", "", "", UpdateHost: "download.prod.test");
        var off = HostedEndpoints.Resolve(TemnoEnvironment.Production, hostless, _ => null, () => null);
        Assert.Equal(HostedEndpoints.ProductionNotConfiguredMessage, off.Problem);
        Assert.Equal("https://download.prod.test/windows/stable.json", off.UpdateFeedUrl);
    }

    [Fact]
    public void The_shipped_production_identity_reads_download_temno_io()
    {
        // WP-376: an installed Temno finds its updates with no setting.
        Assert.Equal("download.temno.io", EnvironmentIdentity.Production.UpdateHost);
        Assert.Equal("https://download.temno.io/windows/stable.json", EnvironmentIdentity.Production.UpdateFeedUrl);
        Assert.Equal(EnvironmentIdentity.Production.UpdateFeedUrl,
            HostedEndpoints.Resolve(TemnoEnvironment.Production, EnvironmentIdentity.Production, _ => null, () => null).UpdateFeedUrl);
    }

    [Fact]
    public void The_shipped_update_host_is_not_the_development_host_and_is_refused_by_development()
    {
        var shipped = EnvironmentIdentity.Production;
        Assert.True(shipped.NamesHostOf(shipped.UpdateFeedUrl));
        // Every development host carries "-dev" before the domain, so no host a production build ships may. The
        // development host itself is not named: this file is part of the public source.
        Assert.All(new[] { shipped.GatewayHost, shipped.AccountsHost, shipped.EventsHost, shipped.UpdateHost },
            host => Assert.DoesNotContain("-dev.", host, StringComparison.OrdinalIgnoreCase));

        var backend = HostedEndpoints.Resolve(TemnoEnvironment.Development, EnvironmentIdentity.Production, _ => null,
            () => File(EnvironmentIdentity.Production.UpdateFeedUrl));
        Assert.Equal("", backend.UpdateFeedUrl);
        Assert.Contains("points at production", backend.Problem);
    }

    [Fact]
    public void Development_reads_updateFeedUrl_from_the_developer_file()
    {
        var backend = Dev(DevFeed);

        Assert.Equal(DevFeed, backend.UpdateFeedUrl);
        Assert.Null(backend.Problem);
        Assert.True(backend.AuthConfigured && backend.GatewayConfigured && backend.AccountsConfigured);
    }

    [Fact]
    public void A_feed_needs_none_of_the_backend_values()
    {
        var backend = Dev(DevFeed, backend: false);

        Assert.Equal(DevFeed, backend.UpdateFeedUrl);
        Assert.Equal(HostedEndpoints.DevelopmentNotConfiguredMessage, backend.Problem);
        Assert.False(backend.AuthConfigured);
        Assert.False(backend.GatewayConfigured);
    }

    [Fact]
    public void No_file_or_no_member_means_no_feed()
    {
        Assert.Equal("", Dev(null).UpdateFeedUrl);
        Assert.Equal("", HostedEndpoints.Resolve(TemnoEnvironment.Development, Prod, _ => null, () => null).UpdateFeedUrl);
    }

    [Fact]
    public void The_feed_variable_is_the_setting_override_not_the_build_feed()
    {
        var backend = Dev(null, env: name => name == "TEMNO_UPDATE_FEED_URL" ? DevFeed : null);
        Assert.Equal("", backend.UpdateFeedUrl);
    }

    [Theory]
    [InlineData("https://download.prod.test/windows/stable.json")]
    [InlineData("https://DOWNLOAD.PROD.TEST./windows/stable.json")]
    [InlineData("https://download.prod.test:443/elsewhere.json")]
    [InlineData("https://gateway.prod.test/windows/stable.json")]
    public void Development_refuses_a_feed_on_a_production_host(string feed)
    {
        var backend = Dev(feed);

        Assert.Equal("", backend.UpdateFeedUrl);
        Assert.False(backend.AuthConfigured);
        Assert.Contains("\"updateFeedUrl\"", backend.Problem);
        Assert.Contains("points at production", backend.Problem);
    }

    [Theory]
    [InlineData("http://download.dev.test/windows/stable.json")]
    [InlineData("file:///C:/feeds/stable.json")]
    [InlineData(@"C:\feeds\stable.json")]
    [InlineData("download.dev.test/windows/stable.json")]
    [InlineData("https://user:secret@download.dev.test/windows/stable.json")]
    [InlineData("https://download.dev.test/windows/stable.json#x")]
    public void A_feed_that_is_not_plain_https_turns_hosted_features_off(string feed)
    {
        var backend = Dev(feed);

        Assert.Equal("", backend.UpdateFeedUrl);
        Assert.False(backend.AuthConfigured);
        Assert.Contains("\"updateFeedUrl\"", backend.Problem);
        Assert.Contains("https", backend.Problem);
    }

    [Fact]
    public void An_updateFeedUrl_that_is_not_text_is_refused_like_the_others()
    {
        Assert.False(DevelopmentBackendFile.TryParse("""{"environment":"development","updateFeedUrl":7}""", out _, out var problem));
        Assert.Contains("updateFeedUrl", problem);
        Assert.True(DevelopmentBackendFile.TryParse($$"""{"environment":"development","updateFeedUrl":"{{DevFeed}}"}""", out var values, out _));
        Assert.Equal(DevFeed, values.UpdateFeedUrl);
    }

    [Fact]
    public void The_update_host_is_one_of_the_identity_s_hosts()
    {
        Assert.True(Prod.NamesHost("download.prod.test"));
        Assert.True(Prod.NamesHostOf(Prod.UpdateFeedUrl));
        Assert.True(Prod.CoversHost("*.prod.test"));
        Assert.False(Prod.NamesHostOf(DevFeed));

        var onlyUpdates = new EnvironmentIdentity("", "", "", "", "", "", UpdateHost: "download.prod.test");
        Assert.True(onlyUpdates.HasHosts);
        Assert.True(onlyUpdates.CoversHost("*.PROD.TEST."));
        Assert.False(new EnvironmentIdentity("", "", "", "", "", "").HasHosts);
    }

    [Theory]
    [InlineData("https://download.prod.test/windows/stable.json")]
    [InlineData("https://GATEWAY.PROD.TEST./elsewhere.json")]
    public async Task A_development_build_reads_no_feed_on_a_production_host_wherever_the_URL_came_from(string feed)
    {
        // The variable and the Settings field reach the checker unfiltered; the checker holds the line (WP-375 T1).
        var handler = new FeedHandler();
        var result = await new UpdateChecker(handler, Prod).CheckAsync(feed, "0.1.0.0");

        Assert.Equal(UpdateCheckKind.Error, result.Kind);
        Assert.Equal(UpdateChecker.ProductionFeedMessage, result.Message);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task A_feed_that_lands_on_a_production_host_is_refused()
    {
        var handler = new FeedHandler(new Uri("https://download.prod.test/windows/stable.json"));
        var result = await new UpdateChecker(handler, Prod).CheckAsync(DevFeed, "0.1.0.0");

        Assert.Equal(UpdateChecker.ProductionFeedMessage, result.Message);
    }

    [Fact]
    public async Task A_production_build_reads_its_own_feed()
    {
        var handler = new FeedHandler();
        var result = await new UpdateChecker(handler, refusedHosts: null).CheckAsync(Prod.UpdateFeedUrl, "0.1.0.0");

        Assert.Equal(UpdateCheckKind.UpdateAvailable, result.Kind);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task This_development_build_refuses_the_shipped_production_hosts()
    {
        var handler = new FeedHandler();
        var result = await new UpdateChecker(handler).CheckAsync(
            $"https://{EnvironmentIdentity.Production.GatewayHost}/windows/stable.json", "0.1.0.0");

        Assert.Equal(UpdateChecker.ProductionFeedMessage, result.Message);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public void The_variable_beats_the_setting_which_beats_the_build()
    {
        var prefs = new UserPreferences();
        Assert.Null(prefs.ResolveUpdateFeedUrl(null, TemnoEnvironment.Development));
        Assert.Equal(DevFeed, prefs.ResolveUpdateFeedUrl(" " + DevFeed + " ", TemnoEnvironment.Development));

        prefs.Updates.FeedUrl = @"C:\feeds\local.json";
        Assert.Equal(@"C:\feeds\local.json", prefs.ResolveUpdateFeedUrl(DevFeed, TemnoEnvironment.Development));

        Environment.SetEnvironmentVariable("TEMNO_UPDATE_FEED_URL", "https://override.dev.test/feed.json");
        Assert.Equal("https://override.dev.test/feed.json", prefs.ResolveUpdateFeedUrl(DevFeed, TemnoEnvironment.Development));
    }

    [Fact]
    public void Production_reads_no_saved_feed_field_because_it_shows_none()
    {
        // WP-376: the Settings field is development-only, so a value saved before cannot steer production unseen.
        var prefs = new UserPreferences();
        prefs.Updates.FeedUrl = "https://elsewhere.test/stable.json";
        Assert.Equal(Prod.UpdateFeedUrl, prefs.ResolveUpdateFeedUrl(Prod.UpdateFeedUrl, TemnoEnvironment.Production));
        Assert.Null(prefs.ResolveUpdateFeedUrl("", TemnoEnvironment.Production));

        // The variable remains an operator's override, as for WP-266's local-feed receipt.
        Environment.SetEnvironmentVariable("TEMNO_UPDATE_FEED_URL", @"C:\feeds\local.json");
        Assert.Equal(@"C:\feeds\local.json", prefs.ResolveUpdateFeedUrl(Prod.UpdateFeedUrl, TemnoEnvironment.Production));
    }

    [Fact]
    public async Task Production_reads_a_404_from_its_own_feed_as_no_release_yet()
    {
        // WP-376: Temno ships before its first release reaches download.temno.io, so the built-in feed answers 404.
        var handler = new StatusHandler(System.Net.HttpStatusCode.NotFound);
        var result = await new UpdateChecker(handler, refusedHosts: null, ownFeed: Prod.UpdateFeedUrl).CheckAsync(Prod.UpdateFeedUrl, "0.1.0.0");

        Assert.Equal(UpdateCheckKind.UpToDate, result.Kind);
        Assert.Equal("You're up to date (0.1.0.0).", result.Message);
        Assert.Null(result.FeedVersion);
        Assert.Null(result.AppInstallerUrl);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("https://elsewhere.prod.test/windows/stable.json", System.Net.HttpStatusCode.NotFound, "HTTP 404")]
    [InlineData("https://download.prod.test/windows/stable.json", System.Net.HttpStatusCode.InternalServerError, "HTTP 500")]
    [InlineData("https://download.prod.test/windows/stable.json", System.Net.HttpStatusCode.Forbidden, "HTTP 403")]
    public async Task Any_other_feed_or_status_is_still_an_error(string feed, System.Net.HttpStatusCode status, string expected)
    {
        var result = await new UpdateChecker(new StatusHandler(status), refusedHosts: null, ownFeed: Prod.UpdateFeedUrl).CheckAsync(feed, "0.1.0.0");

        Assert.Equal(UpdateCheckKind.Error, result.Kind);
        Assert.Contains(expected, result.Message);
    }

    [Fact]
    public async Task A_development_build_still_reports_its_feed_s_404()
    {
        // No own feed: this test assembly is a development build, so a mistyped developer feed is not hidden.
        var result = await new UpdateChecker(new StatusHandler(System.Net.HttpStatusCode.NotFound)).CheckAsync(DevFeed, "0.1.0.0");

        Assert.Equal(UpdateCheckKind.Error, result.Kind);
        Assert.Contains("HTTP 404", result.Message);
    }

    private sealed class StatusHandler(System.Net.HttpStatusCode status) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("<!doctype html>"), RequestMessage = request });
        }
    }

    private sealed class FeedHandler(Uri? landedOn = null) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{"version":"0.1.0.2","appInstallerUrl":"https://download.dev.test/windows/0.1.0.2/Temno-Dev_0.1.0.2_x64.msix"}"""),
                RequestMessage = landedOn is null ? request : new HttpRequestMessage(HttpMethod.Get, landedOn),
            });
        }
    }
}
