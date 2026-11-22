using System.Net;
using System.Text;
using Temno.Core.Account;
using Temno.Core.Agent;
using Xunit;

namespace Temno.Core.Tests;

public sealed class HostedAgentReconnectTests
{
    private static string Event(int id, string kind, string data) => $"id: {id}\nevent: {kind}\ndata: {data}\n\n";
    private static HttpResponseMessage Sse(string data) => new(HttpStatusCode.OK)
        { Content = new StringContent(data, Encoding.UTF8, "text/event-stream") };
    private static async Task<List<HostedSseEvent>> Read(HostedAgentClient client, CancellationToken ct = default)
    {
        var result = new List<HostedSseEvent>();
        await foreach (var evt in client.StreamEventsAsync(Guid.NewGuid(), 0, ct)) result.Add(evt);
        return result;
    }

    [Fact]
    public async Task Several_disconnects_resume_the_cursor_and_do_not_repeat_applied_text()
    {
        var clock = new Clock();
        using var transport = new Transport((n, _) => Task.FromResult(n switch
        {
            1 => Sse(Event(1, "delta", "{\"text\":\"first\"}")),
            2 => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            3 => throw new HttpRequestException("restart"),
            _ => Sse(Event(1, "delta", "{\"text\":\"first\"}") + Event(2, "delta", "{\"text\":\" last\"}")
                + Event(3, "terminal", "{\"status\":\"succeeded\",\"charge\":5}")),
        }));
        using var http = new HttpClient(transport);
        var result = await Pump(Read(new("https://fixture.test", () => "fixture", http, timeProvider: clock)), clock);
        Assert.Equal(new[] { 1, 2, 3 }, result.Select(x => x.Sequence));
        Assert.Equal(new[] { 0, 1, 1, 1 }, transport.Cursors);
        Assert.All(transport.Methods, method => Assert.Equal(HttpMethod.Get, method));
    }

    [Theory]
    [InlineData(408)] [InlineData(429)] [InlineData(500)] [InlineData(502)] [InlineData(503)] [InlineData(504)]
    public async Task Temporary_http_failure_is_retried(int status)
    {
        var clock = new Clock();
        using var transport = new Transport((n, _) => Task.FromResult(n == 1
            ? new HttpResponseMessage((HttpStatusCode)status)
            : Sse(Event(1, "terminal", "{\"status\":\"failed\",\"charge\":0}"))));
        using var http = new HttpClient(transport);
        Assert.Single(await Pump(Read(new("https://fixture.test", () => "fixture", http, timeProvider: clock)), clock));
        Assert.Equal(2, transport.Cursors.Count);
    }

    [Theory]
    [InlineData(401)] [InlineData(403)] [InlineData(404)] [InlineData(410)]
    public async Task Authentication_or_permanent_failure_is_returned_at_once(int status)
    {
        using var transport = new Transport((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)));
        using var http = new HttpClient(transport);
        var ex = await Assert.ThrowsAsync<HostedApiException>(() => Read(new("https://fixture.test", () => "fixture", http)));
        Assert.Equal(status, ex.StatusCode);
        Assert.Single(transport.Cursors);
    }

    [Fact]
    public async Task Empty_streams_cannot_reset_the_five_minute_recovery_bound()
    {
        var clock = new Clock();
        using var transport = new Transport((_, _) => Task.FromResult(Sse("")));
        using var http = new HttpClient(transport);
        var ex = await Assert.ThrowsAsync<HostedApiException>(() => Pump(
            Read(new("https://fixture.test", () => "fixture", http, timeProvider: clock)), clock));
        Assert.Contains("five minutes", ex.Message);
        Assert.InRange(clock.GetUtcNow() - DateTimeOffset.UnixEpoch, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(6));
        Assert.InRange(transport.Cursors.Count, 30, 50);
    }

    [Fact]
    public async Task Cancellation_interrupts_backoff_without_another_connection()
    {
        using var stop = new CancellationTokenSource();
        using var transport = new Transport((_, _) => Task.FromResult(Sse("")));
        using var http = new HttpClient(transport);
        var read = Read(new("https://fixture.test", () => "fixture", http), stop.Token);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        Assert.Single(transport.Cursors);
    }

    [Fact]
    public async Task An_unresponsive_reconnect_cannot_outlive_the_recovery_bound()
    {
        var clock = new Clock();
        using var transport = new Transport(async (n, ct) =>
        {
            if (n > 1) await Task.Delay(Timeout.Infinite, ct);
            return Sse("");
        });
        using var http = new HttpClient(transport);
        await Assert.ThrowsAsync<HostedApiException>(() => Pump(
            Read(new("https://fixture.test", () => "fixture", http, timeProvider: clock)), clock));
        Assert.InRange(clock.GetUtcNow() - DateTimeOffset.UnixEpoch, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(6));
    }

    [Fact]
    public async Task A_broken_body_is_retried_but_invalid_events_are_not()
    {
        var clock = new Clock();
        using var transport = new Transport((n, _) => Task.FromResult(n == 1
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BrokenStream()) }
            : Sse("id: 1\nevent: delta\ndata: INVALID\n\n")));
        using var http = new HttpClient(transport);
        await Assert.ThrowsAsync<HostedApiException>(() => Pump(
            Read(new("https://fixture.test", () => "fixture", http, timeProvider: clock)), clock));
        Assert.Equal(2, transport.Cursors.Count);
    }

    private sealed class BrokenStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromException<int>(new IOException("connection reset"));
    }

    [Fact]
    public async Task A_silent_half_open_body_is_reconnected_after_the_idle_timeout()
    {
        var clock = new Clock();
        using var transport = new Transport((n, _) => Task.FromResult(n == 1
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new SilentStream()) }
            : Sse(Event(1, "terminal", "{\"status\":\"succeeded\",\"charge\":5}"))));
        using var http = new HttpClient(transport);
        Assert.Single(await Pump(Read(new("https://fixture.test", () => "fixture", http, timeProvider: clock)), clock));
        Assert.Equal(2, transport.Cursors.Count);
        Assert.InRange(clock.GetUtcNow() - DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(46), TimeSpan.FromSeconds(60));
    }

    private sealed class SilentStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    [Fact]
    public async Task A_temporary_error_with_a_stalled_body_is_reconnected_after_the_read_timeout()
    {
        var clock = new Clock();
        using var transport = new Transport((n, _) => Task.FromResult(n == 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new SilentErrorContent() }
            : Sse(Event(1, "terminal", "{\"status\":\"succeeded\",\"charge\":5}"))));
        using var http = new HttpClient(transport);
        Assert.Single(await Pump(Read(new("https://fixture.test", () => "fixture", http, timeProvider: clock)), clock));
        Assert.Equal(2, transport.Cursors.Count);
        Assert.InRange(clock.GetUtcNow() - DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(46), TimeSpan.FromSeconds(60));
    }

    private sealed class SilentErrorContent : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => Task.Delay(Timeout.Infinite);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct)
            => Task.Delay(Timeout.Infinite, ct);
    }

    private sealed class Transport(Func<int, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<int> Cursors { get; } = [];
        public List<HttpMethod> Methods { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Cursors.Add(request.Headers.TryGetValues("Last-Event-ID", out var values) ? int.Parse(values.Single()) : 0);
            Methods.Add(request.Method);
            return respond(Cursors.Count, ct);
        }
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(409)]
    [InlineData(413)]
    public async Task Permanent_tool_submission_errors_are_returned_without_retry(int status)
    {
        using var transport = new Transport((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)));
        using var http = new HttpClient(transport);
        var client = new HostedAgentClient("https://fixture.test", () => "fixture", http);
        var ex = await Assert.ThrowsAsync<HostedApiException>(() => client.SubmitToolResultsAsync(
            Guid.NewGuid(), [new() { Id = "call_1", Content = "result" }]));
        Assert.Equal(status, ex.StatusCode);
        Assert.Equal(new[] { HttpMethod.Post }, transport.Methods);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tool_submission_retries_end_within_the_recovery_window(bool stallRetry)
    {
        var clock = new Clock();
        using var transport = new Transport(async (n, ct) =>
        {
            if (stallRetry && n > 1) await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });
        using var http = new HttpClient(transport);
        var client = new HostedAgentClient("https://fixture.test", () => "fixture", http, timeProvider: clock);
        var task = client.SubmitToolResultsAsync(Guid.NewGuid(), [new() { Id = "call_1", Content = "result" }]);
        var ex = await Assert.ThrowsAsync<HostedApiException>(() => Pump(task, clock));
        Assert.Contains("five minutes", ex.Message);
        Assert.InRange(clock.GetUtcNow() - DateTimeOffset.UnixEpoch, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(6));
        Assert.All(transport.Methods, method => Assert.Equal(HttpMethod.Post, method));
        Assert.InRange(transport.Methods.Count, 2, 50);
    }

    private static async Task<T> Pump<T>(Task<T> task, Clock clock)
    {
        await Pump((Task)task, clock);
        return await task;
    }

    private static async Task Pump(Task task, Clock clock)
    {
        for (var i = 0; !task.IsCompleted && i < 400; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(2);
        }
        await task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class Clock : TimeProvider
    {
        private readonly List<Timer> _timers = [];
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Timer(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }
        public void Advance(TimeSpan amount)
        {
            List<Timer> due;
            lock (_timers)
            {
                _ticks += amount.Ticks;
                due = _timers.Where(t => t.Due <= _ticks).ToList();
                foreach (var timer in due) _timers.Remove(timer);
            }
            foreach (var timer in due) timer.Fire();
        }
        private sealed class Timer(Clock clock, TimerCallback callback, object? state) : ITimer
        {
            public long Due;
            private bool _disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (clock._timers)
                {
                    if (_disposed) return false;
                    clock._timers.Remove(this);
                    if (dueTime != Timeout.InfiniteTimeSpan)
                    {
                        Due = clock._ticks + dueTime.Ticks;
                        clock._timers.Add(this);
                    }
                    return true;
                }
            }
            public void Fire() { if (!_disposed) callback(state); }
            public void Dispose() { lock (clock._timers) { _disposed = true; clock._timers.Remove(this); } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
