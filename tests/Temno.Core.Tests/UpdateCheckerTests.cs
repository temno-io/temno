using System.Net;
using System.Net.Http;
using System.Text;
using Temno.Core.Updates;
using Xunit;

namespace Temno.Core.Tests;

public sealed class UpdateCheckerTests
{
    [Fact]
    public async Task EmptyUrl_DoesNotCallHttp()
    {
        var handler = new ThrowingHandler();
        var checker = new UpdateChecker(handler);
        var result = await checker.CheckAsync("", "0.1.0-windows");
        Assert.Equal(UpdateCheckKind.Unconfigured, result.Kind);
        Assert.Equal(UpdateChecker.UnconfiguredMessage, result.Message);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task NullUrl_DoesNotCallHttp()
    {
        var handler = new ThrowingHandler();
        var checker = new UpdateChecker(handler);
        var result = await checker.CheckAsync(null, "0.1.0-windows");
        Assert.Equal(UpdateCheckKind.Unconfigured, result.Kind);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task NewerFeedVersion_UpdateAvailable()
    {
        const string url = "https://example.test/palmier/feed.json";
        const string installer = "https://example.test/palmier/Temno.App.appinstaller";
        var handler = new StubHandler(
            """{"version":"0.2.0","appInstallerUrl":"https://example.test/palmier/Temno.App.appinstaller"}""");
        var checker = new UpdateChecker(handler);
        var result = await checker.CheckAsync(url, "0.1.0-windows");
        Assert.Equal(UpdateCheckKind.UpdateAvailable, result.Kind);
        Assert.Equal("0.2.0", result.FeedVersion);
        Assert.Equal(installer, result.AppInstallerUrl);
        Assert.Contains("0.2.0", result.Message, StringComparison.Ordinal);
        Assert.Contains(installer, result.Message, StringComparison.Ordinal);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(url, handler.LastUri?.ToString());
    }

    [Fact]
    public async Task SameNumericVersion_UpToDate()
    {
        var handler = new StubHandler("""{"version":"0.1.0","appInstallerUrl":"https://example.test/x.appinstaller"}""");
        var checker = new UpdateChecker(handler);
        var result = await checker.CheckAsync("https://example.test/feed.json", "0.1.0-windows");
        Assert.Equal(UpdateCheckKind.UpToDate, result.Kind);
        Assert.Contains("0.1.0-windows", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LeadingVOnFeedVersion_StillCompares()
    {
        var handler = new StubHandler("""{"version":"v0.2.0","appInstallerUrl":"https://example.test/x.appinstaller"}""");
        var checker = new UpdateChecker(handler);
        var result = await checker.CheckAsync("https://example.test/feed.json", "0.1.0-windows");
        Assert.Equal(UpdateCheckKind.UpdateAvailable, result.Kind);
        Assert.Equal("v0.2.0", result.FeedVersion);
    }

    [Fact]
    public async Task LocalFile_NewerVersion_DoesNotCallHttp()
    {
        var path = Path.Combine(Path.GetTempPath(), "temno-feed-" + Guid.NewGuid().ToString("N") + ".json");
        var handler = new ThrowingHandler();
        try
        {
            await File.WriteAllTextAsync(path, """{"version":"9.0.0","appInstallerUrl":"https://example.test/Temno.App.appinstaller"}""");
            var checker = new UpdateChecker(handler);
            var result = await checker.CheckAsync(path, "0.1.0-windows");
            Assert.Equal(UpdateCheckKind.UpdateAvailable, result.Kind);
            Assert.Equal("9.0.0", result.FeedVersion);
            Assert.Equal(0, handler.Calls);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task FileUri_NewerVersion_UpdateAvailable()
    {
        var path = Path.Combine(Path.GetTempPath(), "temno-feed-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await File.WriteAllTextAsync(path, """{"version":"2.0.0"}""");
            var checker = new UpdateChecker();
            var result = await checker.CheckAsync(new Uri(path).AbsoluteUri, "0.1.0");
            Assert.Equal(UpdateCheckKind.UpdateAvailable, result.Kind);
            Assert.Equal("2.0.0", result.FeedVersion);
            Assert.Null(result.AppInstallerUrl);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task OlderFeedVersion_UpToDate()
    {
        var handler = new StubHandler("""{"version":"0.0.9","appInstallerUrl":"https://example.test/x.msix"}""");
        var checker = new UpdateChecker(handler);
        var result = await checker.CheckAsync("https://example.test/feed.json", "0.1.0-windows");
        Assert.Equal(UpdateCheckKind.UpToDate, result.Kind);
        Assert.Equal("0.0.9", result.FeedVersion);
    }

    [Fact]
    public async Task HttpFailure_ReportsStatus()
    {
        var handler = new StatusHandler(HttpStatusCode.NotFound);
        var checker = new UpdateChecker(handler);
        var result = await checker.CheckAsync("https://example.test/feed.json", "0.1.0");
        Assert.Equal(UpdateCheckKind.Error, result.Kind);
        Assert.Contains("HTTP 404", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("hosted update channel", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConnectionFailure_ReportsUnreachable()
    {
        var handler = new ThrowingHandler(new HttpRequestException("No such host is known."));
        var checker = new UpdateChecker(handler);
        var result = await checker.CheckAsync("https://example.test/feed.json", "0.1.0");
        Assert.Equal(UpdateCheckKind.Error, result.Kind);
        Assert.Equal(UpdateChecker.UnreachableMessage, result.Message);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Timeout_ReportsUnreachable_WithoutThrowing()
    {
        var handler = new ThrowingHandler(new TaskCanceledException());
        var checker = new UpdateChecker(handler);
        var result = await checker.CheckAsync("https://example.test/feed.json", "0.1.0");
        Assert.Equal(UpdateCheckKind.Error, result.Kind);
        Assert.Equal(UpdateChecker.UnreachableMessage, result.Message);
    }

    [Fact]
    public async Task MissingLocalFile_Error()
    {
        var path = Path.Combine(Path.GetTempPath(), "temno-missing-" + Guid.NewGuid().ToString("N") + ".json");
        var checker = new UpdateChecker();
        var result = await checker.CheckAsync(path, "0.1.0");
        Assert.Equal(UpdateCheckKind.Error, result.Kind);
        Assert.Contains("not found", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("http://example.test/feed.json")]
    [InlineData("HTTP://example.test/feed.json")]
    public async Task HttpFeed_IsRefused_WithoutARequest(string url)
    {
        var handler = new ThrowingHandler();
        var result = await new UpdateChecker(handler).CheckAsync(url, "0.1.0");
        Assert.Equal(UpdateCheckKind.Error, result.Kind);
        Assert.Equal(UpdateChecker.HttpsOnlyMessage, result.Message);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(@"\\attacker.example\share\feed.json")]
    [InlineData("//attacker.example/share/feed.json")]
    [InlineData(@"\/attacker.example/share/feed.json")]
    [InlineData("file://attacker.example/share/feed.json")]
    [InlineData("file:////attacker.example/share/feed.json")]
    [InlineData("file:///%2F%2Fattacker.example/share/feed.json")]
    public async Task NetworkShareFeed_IsRefused(string url)
    {
        var handler = new ThrowingHandler();
        var result = await new UpdateChecker(handler).CheckAsync(url, "0.1.0");
        Assert.Equal(UpdateCheckKind.Error, result.Kind);
        Assert.Equal(UpdateChecker.NetworkShareMessage, result.Message);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task FeedRedirectedToHttp_IsRefused()
    {
        var handler = new StubHandler(
            """{"version":"9.0.0","appInstallerUrl":"https://example.test/Temno.App.msix"}""",
            new Uri("http://example.test/feed.json"));
        var result = await new UpdateChecker(handler).CheckAsync("https://example.test/feed.json", "0.1.0");
        Assert.Equal(UpdateCheckKind.Error, result.Kind);
        Assert.Equal(UpdateChecker.LeftHttpsMessage, result.Message);
    }

    [Fact]
    public async Task FeedLocality_IsReported()
    {
        var path = Path.Combine(Path.GetTempPath(), "temno-feed-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await File.WriteAllTextAsync(path, """{"version":"9.0.0","appInstallerUrl":"C:\\updates\\Temno.App.msix"}""");
            var local = await new UpdateChecker(new ThrowingHandler()).CheckAsync(path, "0.1.0");
            Assert.Equal(UpdateCheckKind.UpdateAvailable, local.Kind);
            Assert.True(local.FeedIsLocal);

            var online = await new UpdateChecker(new StubHandler("""{"version":"9.0.0"}"""))
                .CheckAsync("https://example.test/feed.json", "0.1.0");
            Assert.Equal(UpdateCheckKind.UpdateAvailable, online.Kind);
            Assert.False(online.FeedIsLocal);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task OversizedFeed_IsNotRead()
    {
        var huge = "{\"version\":\"9.0.0\",\"pad\":\"" + new string('x', 2 * 1024 * 1024) + "\"}";
        var result = await new UpdateChecker(new StubHandler(huge)).CheckAsync("https://example.test/feed.json", "0.1.0");
        Assert.Equal(UpdateCheckKind.Error, result.Kind);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly Exception _error;

        public ThrowingHandler(Exception? error = null)
            => _error = error ?? new InvalidOperationException("HTTP should not be called for an empty update feed.");

        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            throw _error;
        }
    }

    private sealed class StatusHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;

        public StatusHandler(HttpStatusCode status) => _status = status;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent("missing", Encoding.UTF8, "text/plain"),
            });
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly Uri? _finalUri;

        public StubHandler(string body, Uri? finalUri = null)
        {
            _body = body;
            _finalUri = finalUri;
        }

        public int Calls { get; private set; }
        public Uri? LastUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            LastUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
                RequestMessage = _finalUri is null ? request : new HttpRequestMessage(HttpMethod.Get, _finalUri),
            });
        }
    }
}
