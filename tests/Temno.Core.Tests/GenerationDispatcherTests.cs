using System.Diagnostics;
using System.Net;
using System.Reflection;
using Temno.Generation;
using Temno.Mcp;
using Xunit;
using Xunit.Abstractions;

namespace Temno.Core.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GenerationDispatcherCollection
{
    public const string Name = "GenerationDispatcher";
}

/// <summary>
/// WP-240: the BYOK default-model lookup runs on the dispatcher-bound generation handlers and
/// blocks on the model catalog, so the catalog client must never capture the caller's
/// SynchronizationContext. These tests pin a UI-like context (posted continuations never run
/// while the test thread blocks, exactly like a DispatcherQueueSynchronizationContext whose
/// thread sits in GetResult) and drive the real <c>ToolExecutor.GetDefaultModel</c> seam
/// through the real <c>HttpGenerationClient</c> against a delayed scripted catalog.
/// <para>
/// Pre-fix this completed only via the 5-second <c>WaitAsync</c> bound and returned the
/// hard-coded fallback id; catalog faults were swallowed into the same fallback. Post-fix the
/// catalog entry wins and faults stay observable.
/// </para>
/// </summary>
[Collection(GenerationDispatcherCollection.Name)]
public sealed class GenerationDispatcherTests : IDisposable
{
    private const string FallbackVideoModel = "video-gen-v1";
    private const string CatalogVideoModel = "byok-video-1";
    private static readonly TimeSpan CatalogDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan LookupBound = TimeSpan.FromSeconds(4);

    private readonly ITestOutputHelper _output;
    private GenerationService? _previousOverride;

    public GenerationDispatcherTests(ITestOutputHelper output)
    {
        _output = output;
        _previousOverride = ToolExecutor.GenerationServiceOverride;
    }

    /// <summary>
    /// A delayed catalog under a blocked UI-like context must still resolve to the returned
    /// catalog entry — not the timeout fallback — and well inside the 5-second lookup bound.
    /// </summary>
    [Fact]
    public void DefaultModelLookup_ReturnsCatalogEntry_UnderBlockedSyncContext()
    {
        using var http = new HttpClient(new DelayedCatalogHandler(CatalogDelay));
        ToolExecutor.GenerationServiceOverride =
            new GenerationService(new HttpGenerationClient("https://api.example.test", "test-key", http));

        var context = new NonPumpingSyncContext();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            var sw = Stopwatch.StartNew();
            var selected = InvokeGetDefaultModel(GenerationKind.Video, FallbackVideoModel);
            sw.Stop();

            _output.WriteLine($"selected={selected} elapsedMs={sw.Elapsed.TotalMilliseconds:F0} postedContinuations={context.PostedCount}");
            Assert.Equal(CatalogVideoModel, selected);
            Assert.True(sw.Elapsed < LookupBound,
                $"Default-model lookup took {sw.Elapsed.TotalMilliseconds:F0} ms for a {CatalogDelay.TotalMilliseconds:F0} ms catalog; the continuation was posted to the blocked context.");
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
            context.Drain();
            http.Dispose();
        }
    }

    /// <summary>
    /// A faulting catalog must surface instead of silently submitting a hard-coded fallback id.
    /// </summary>
    [Fact]
    public void DefaultModelLookup_CatalogFault_PropagatesInsteadOfFallback()
    {
        using var http = new HttpClient(new DelayedCatalogHandler(CatalogDelay, Fault: new InvalidOperationException("catalog-boom")));
        ToolExecutor.GenerationServiceOverride =
            new GenerationService(new HttpGenerationClient("https://api.example.test", "test-key", http));

        var context = new NonPumpingSyncContext();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            var thrown = Assert.Throws<TargetInvocationException>(
                () => InvokeGetDefaultModel(GenerationKind.Video, FallbackVideoModel));
            Assert.IsType<InvalidOperationException>(thrown.InnerException);
            Assert.Equal("catalog-boom", thrown.InnerException!.Message);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
            context.Drain();
            http.Dispose();
        }
    }

    /// <summary>
    /// A cancelled catalog must surface as cancellation instead of the fallback id.
    /// </summary>
    [Fact]
    public void DefaultModelLookup_CatalogCancelled_PropagatesInsteadOfFallback()
    {
        using var http = new HttpClient(new CancelledCatalogHandler());
        ToolExecutor.GenerationServiceOverride =
            new GenerationService(new HttpGenerationClient("https://api.example.test", "test-key", http));

        var context = new NonPumpingSyncContext();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            var thrown = Assert.Throws<TargetInvocationException>(
                () => InvokeGetDefaultModel(GenerationKind.Video, FallbackVideoModel));
            Assert.IsAssignableFrom<OperationCanceledException>(thrown.InnerException);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
            context.Drain();
            http.Dispose();
        }
    }

    /// <summary>
    /// WP-329: hosted generation is quote-only, so the hosted client lists no model and has no
    /// default to fall back to; neither lookup sends a request.
    /// </summary>
    [Fact]
    public void Hosted_ListsNoModelAndHasNoDefault()
    {
        var requests = 0;
        using var http = new HttpClient(new CountingHandler(() => requests++));
        ToolExecutor.GenerationServiceOverride = new GenerationService(
            new HostedGenerationClient("https://gateway.example.test", () => "token", http));

        var thrown = Assert.Throws<TargetInvocationException>(
            () => InvokeGetDefaultModel(GenerationKind.Video, FallbackVideoModel));
        Assert.IsType<InvalidOperationException>(thrown.InnerException);
        Assert.Contains("quote-only", thrown.InnerException!.Message);

        var listed = ToolExecutor.Execute("list_models", new System.Text.Json.Nodes.JsonObject());
        Assert.False(listed.IsError, listed.Content[0].Text);
        Assert.Empty(System.Text.Json.Nodes.JsonNode.Parse(listed.Content[0].Text!)!["models"]!.AsArray());
        Assert.Equal(0, requests);
    }

    private static string InvokeGetDefaultModel(GenerationKind kind, string fallback)
    {
        var method = typeof(ToolExecutor).GetMethod(
            "GetDefaultModel", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("ToolExecutor.GetDefaultModel seam not found.");
        return (string)method.Invoke(null, [kind, fallback])!;
    }

    /// <summary>
    /// Behaves like the WinUI DispatcherQueueSynchronizationContext under a blocked caller:
    /// posted continuations queue but never run until <see cref="Drain"/>. The test thread
    /// never awaits while installed, so it can never post to itself.
    /// </summary>
    private sealed class NonPumpingSyncContext : SynchronizationContext
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queue = new();

        public int PostedCount => _queue.Count;

        public override void Post(SendOrPostCallback d, object? state) => _queue.Enqueue((d, state));

        public override void Send(SendOrPostCallback d, object? state) => d(state);

        public void Drain()
        {
            while (_queue.TryDequeue(out var item)) item.Callback(item.State);
        }

        public override SynchronizationContext CreateCopy() => this;
    }

    /// <summary>
    /// Scripted BYOK catalog backend. The delay completes off-context
    /// (<c>ConfigureAwait(false)</c>), exactly like real socket I/O: only the client's own
    /// continuation capture decides whether the lookup survives a blocked caller.
    /// </summary>
    private sealed class DelayedCatalogHandler : HttpMessageHandler
    {
        private readonly TimeSpan _delay;
        private readonly Exception? _fault;

        public DelayedCatalogHandler(TimeSpan delay, Exception? Fault = null)
        {
            _delay = delay;
            _fault = Fault;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(_delay, cancellationToken).ConfigureAwait(false);
            if (_fault is not null) throw _fault;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """[{"id":"byok-video-1","kind":"video","displayName":"BYOK Video"}]"""),
            };
        }
    }

    private sealed class CountingHandler(Action onRequest) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            onRequest();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }
    }

    private sealed class CancelledCatalogHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromCanceled<HttpResponseMessage>(new CancellationToken(canceled: true));
    }

    public void Dispose()
    {
        ToolExecutor.GenerationServiceOverride = _previousOverride;
        GC.SuppressFinalize(this);
    }
}
