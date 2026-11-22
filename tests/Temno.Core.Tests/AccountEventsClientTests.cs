using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Temno.Core.Account;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>WP-350: the app's account events client against an in-memory events Worker.</summary>
public sealed class AccountEventsClientTests
{
    /// <summary>The Worker's behaviour as docs/contracts/account-events.md states it, kept in memory.</summary>
    private sealed class Server : IAccountEventsTransport
    {
        private readonly object _gate = new();
        private readonly List<(long Seq, string Kind, string Id)> _log = new();
        public string Epoch = "0123456789abcdef0123456789abcdef";
        public long Seq;
        public bool Jobs;
        public bool BlockSockets;
        public AccountEventsRefusedException? RefuseConnect;
        public AccountEventsRefusedException? RefusePoll;
        public long DropOlderThan;
        private readonly List<Uri> _connects = new();
        private readonly List<Uri> _polls = new();
        private readonly List<string> _tokens = new();
        public readonly ConcurrentQueue<Socket> Sockets = new();

        // The client's loop appends while a test reads, so a test only ever sees a copy.
        public IReadOnlyList<Uri> Connects { get { lock (_gate) return _connects.ToArray(); } }
        public IReadOnlyList<Uri> Polls { get { lock (_gate) return _polls.ToArray(); } }
        public IReadOnlyList<string> Tokens { get { lock (_gate) return _tokens.ToArray(); } }

        public Socket? Open => Sockets.LastOrDefault(s => !s.Closed);

        /// <summary>The client has handled every message pushed to every socket it still holds.</summary>
        public bool CaughtUp => Sockets.Where(s => !s.Closed).All(s => s.CaughtUp);

        public void Publish(string kind, string id)
        {
            lock (_gate)
            {
                Seq++;
                _log.Add((Seq, kind, id));
                foreach (var socket in Sockets.Where(s => !s.Closed))
                    socket.Push(JsonSerializer.Serialize(new { type = "event", seq = Seq, kind, id, at = 1 }));
            }
        }

        /// <summary>Events the client must miss because they happen while no socket is open.</summary>
        public void PublishQuietly(string kind, string id)
        {
            lock (_gate)
            {
                Seq++;
                _log.Add((Seq, kind, id));
            }
        }

        public JsonObject Snapshot(Uri url)
        {
            var query = System.Web.HttpUtility.ParseQueryString(url.Query);
            lock (_gate)
            {
                var kept = _log.Where(e => e.Seq > DropOlderThan).ToList();
                var oldest = kept.Count == 0 ? Seq + 1 : kept[0].Seq;
                var after = query["after"] is { } text ? long.Parse(text) : (long?)null;
                var reset = after is null || query["epoch"] != Epoch || after > Seq || after < oldest - 1;
                var events = reset ? new JsonArray() : new JsonArray(kept.Where(e => e.Seq > after)
                    .Select(e => (JsonNode)new JsonObject { ["seq"] = e.Seq, ["kind"] = e.Kind, ["id"] = e.Id, ["at"] = 1 }).ToArray());
                return new JsonObject { ["epoch"] = Epoch, ["seq"] = Seq, ["reset"] = reset, ["jobs"] = Jobs, ["events"] = events };
            }
        }

        public Task<IAccountEventsSocket> ConnectAsync(Uri url, string token, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _connects.Add(url);
                _tokens.Add(token);
                if (RefuseConnect is { } refused)
                    throw refused;
                if (BlockSockets)
                    throw new AccountEventsRefusedException(403, null, null);
                var socket = new Socket();
                var hello = Snapshot(url);
                hello["type"] = "hello";
                hello["exp"] = 1;
                socket.Push(hello.ToJsonString());
                Sockets.Enqueue(socket);
                return Task.FromResult<IAccountEventsSocket>(socket);
            }
        }

        public Task<string> PollAsync(Uri url, string token, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _polls.Add(url);
                _tokens.Add(token);
                if (RefusePoll is { } refused)
                    throw refused;
                return Task.FromResult(Snapshot(url).ToJsonString());
            }
        }
    }

    private sealed class Socket : IAccountEventsSocket
    {
        private readonly Channel<string?> _inbound = Channel.CreateUnbounded<string?>();
        private readonly object _gate = new();
        private int _unread;
        private bool _handling;
        public readonly ConcurrentQueue<string> Sent = new();
        public volatile bool Closed;
        public volatile bool Silent;
        public int? CloseCode { get; private set; }

        /// <summary>
        /// Nothing pushed is unread, and the client has finished the last message it took: it handles a message
        /// between one <see cref="ReceiveAsync"/> and the next.
        /// </summary>
        public bool CaughtUp
        {
            get { lock (_gate) return _unread == 0 && !_handling; }
        }

        public void Push(string text) => Write(text);

        private void Write(string? text)
        {
            lock (_gate)
                _unread++;
            _inbound.Writer.TryWrite(text);
        }

        public void Close(int? code)
        {
            CloseCode = code;
            Closed = true;
            Write(null);
        }

        public Task SendAsync(string text, CancellationToken cancellationToken)
        {
            Sent.Enqueue(text);
            if (text == """{"type":"ping"}""" && !Silent)
                Push("""{"type":"pong"}""");
            return Task.CompletedTask;
        }

        public async Task<string?> ReceiveAsync(CancellationToken cancellationToken)
        {
            lock (_gate)
                _handling = false;
            var text = await _inbound.Reader.ReadAsync(cancellationToken);
            lock (_gate)
            {
                _unread--;
                _handling = true;
            }

            return text;
        }

        public ValueTask DisposeAsync()
        {
            Closed = true;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Virtual time: every wait the client asks for passes at once on this clock, but only after the client has
    /// handled what its sockets received. Otherwise a busy machine that delays the client's reads by a few
    /// milliseconds lets minutes pass unheard, and the silence check drops a healthy socket.
    /// </summary>
    private sealed class Clock(Func<bool> caughtUp)
    {
        private static readonly TimeSpan CatchUpLimit = TimeSpan.FromSeconds(10);
        private long _ticks = DateTimeOffset.Parse("2026-10-07T00:00:00Z").UtcTicks;
        public readonly ConcurrentQueue<TimeSpan> Waits = new();
        public DateTimeOffset Now => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        public async Task Delay(TimeSpan gap, CancellationToken ct)
        {
            Waits.Enqueue(gap);
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp(gap.TotalMilliseconds / 1000, 1, 5)), ct);
            var limit = DateTime.UtcNow + CatchUpLimit;
            while (!caughtUp() && DateTime.UtcNow < limit)
                await Task.Delay(1, ct);
            Interlocked.Add(ref _ticks, gap.Ticks);
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        public readonly Server Server = new();
        public readonly Clock Clock;
        public readonly ConcurrentQueue<AccountEventsTickle> Tickles = new();
        public readonly ConcurrentQueue<AccountEventsState> States = new();
        public string? Token = "token-1";
        public readonly AccountEventsClient Client;

        public Harness()
        {
            Clock = new Clock(() => Server.CaughtUp);
            Client = new AccountEventsClient(new Uri("https://events.example.test"), () => Token, Server, Clock.Delay, () => Clock.Now, new Random(7));
            Client.Tickled += Tickles.Enqueue;
            Client.StateChanged += States.Enqueue;
        }

        public IEnumerable<string> Kinds => Tickles.Where(t => !t.Reset).SelectMany(t => t.Kinds);
        public int Resets => Tickles.Count(t => t.Reset);

        public async Task Until(Func<bool> condition, string what)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!condition())
            {
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException($"{what}; state {Client.State}, tickles {JsonSerializer.Serialize(Tickles)}");
                await Task.Delay(5);
            }
        }

        public async ValueTask DisposeAsync() => await Client.StopAsync();
    }

    [Fact]
    public async Task It_resumes_after_a_dropped_socket_without_losing_an_event()
    {
        await using var h = new Harness();
        h.Client.Start();
        await h.Until(() => h.Client.State == AccountEventsState.Live, "first socket");
        Assert.Equal(1, h.Resets);
        Assert.Equal("https://events.example.test/v1/events", h.Server.Connects[0].ToString());

        h.Server.Publish("credits", "a");
        h.Server.Publish("job", "b");
        await h.Until(() => h.Kinds.Count() == 2, "two live events");

        h.Server.Open!.Close(null);
        h.Server.PublishQuietly("subscription", "c");
        h.Server.PublishQuietly("chat", "d");
        await h.Until(() => h.Kinds.Count() == 4, "the replay");

        Assert.Equal(["credits", "job", "subscription", "chat"], h.Kinds);
        Assert.Equal(1, h.Resets);
        Assert.Equal($"https://events.example.test/v1/events?after=2&epoch={h.Server.Epoch}", h.Server.Connects[1].ToString());
        h.Server.Publish("credits", "e");
        await h.Until(() => h.Kinds.Count() == 5, "live again");
        Assert.Equal(5, h.Client.Seq);
    }

    [Fact]
    public async Task A_gap_or_a_new_epoch_re_reads_everything()
    {
        await using var h = new Harness();
        h.Client.Start();
        await h.Until(() => h.Client.State == AccountEventsState.Live, "socket");
        h.Server.Publish("credits", "a");
        await h.Until(() => h.Kinds.Count() == 1, "event");

        // Retention dropped what the client missed: the Worker answers the cursor with a reset.
        h.Server.Open!.Close(null);
        h.Server.PublishQuietly("credits", "b");
        h.Server.PublishQuietly("credits", "c");
        h.Server.DropOlderThan = 2;
        await h.Until(() => h.Resets == 2, "reset after the gap");

        // An object that emptied itself starts a new epoch.
        h.Server.Open!.Close(null);
        h.Server.Epoch = "fedcba9876543210fedcba9876543210";
        await h.Until(() => h.Resets == 3, "reset on the new epoch");
        Assert.Equal("fedcba9876543210fedcba9876543210", h.Client.Epoch);
    }

    [Fact]
    public async Task A_live_event_out_of_sequence_is_a_gap_and_a_repeat_is_ignored()
    {
        await using var h = new Harness();
        h.Client.Start();
        await h.Until(() => h.Client.State == AccountEventsState.Live, "socket");
        var socket = h.Server.Open!;
        socket.Push("""{"type":"event","seq":1,"kind":"credits","id":"a","at":1}""");
        socket.Push("""{"type":"event","seq":1,"kind":"credits","id":"a","at":1}""");
        socket.Push("""{"type":"event","seq":3,"kind":"job","id":"c","at":1}""");
        socket.Push("not json");
        socket.Push("""{"type":"event","seq":4,"kind":"chat","id":"d","at":1}""");
        await h.Until(() => h.Client.Seq == 4, "the last event");
        Assert.Equal(["credits", "chat"], h.Kinds);
        Assert.Equal(2, h.Resets);
    }

    [Fact]
    public async Task A_half_open_socket_that_stops_answering_is_dropped_and_resumed()
    {
        await using var h = new Harness();
        h.Client.Start();
        await h.Until(() => h.Client.State == AccountEventsState.Live, "socket");
        var first = h.Server.Open!;
        await h.Until(() => first.Sent.Count(m => m.Contains("ping")) >= 3, "pings answered");
        Assert.Single(h.Server.Connects);
        first.Silent = true;
        h.Server.PublishQuietly("credits", "while-dead");
        await h.Until(() => h.Server.Connects.Count == 2 && h.Client.State == AccountEventsState.Live, "reconnected");
        await h.Until(() => h.Kinds.Contains("credits"), "the missed event");
        Assert.Contains("after=0", h.Server.Connects[1].Query);
    }

    [Fact]
    public async Task The_suspended_close_stops_it_for_good()
    {
        await using var h = new Harness();
        h.Client.Start();
        await h.Until(() => h.Client.State == AccountEventsState.Live, "socket");
        h.Server.Open!.Close(AccountEventCloseCodes.AccountSuspended);
        await h.Until(() => h.Client.State == AccountEventsState.Suspended, "suspended");
        await Task.Delay(100);
        Assert.Single(h.Server.Connects);
        Assert.Empty(h.Server.Polls);
    }

    [Fact]
    public async Task A_refused_socket_for_a_suspended_account_stops_it_too()
    {
        await using var h = new Harness();
        h.Server.RefuseConnect = new AccountEventsRefusedException(403, "account_suspended", null);
        h.Client.Start();
        await h.Until(() => h.Client.State == AccountEventsState.Suspended, "suspended");
        await Task.Delay(100);
        Assert.Single(h.Server.Connects);
    }

    [Fact]
    public async Task An_expired_token_waits_for_the_session_to_refresh_then_reconnects_with_the_new_one()
    {
        await using var h = new Harness();
        h.Client.Start();
        await h.Until(() => h.Client.State == AccountEventsState.Live, "socket");
        h.Server.RefuseConnect = new AccountEventsRefusedException(401, "authentication_required", null);
        h.Server.Open!.Close(AccountEventCloseCodes.AuthenticationRequired);
        await h.Until(() => h.Server.Connects.Count >= 2, "a retry");
        Assert.Contains(AccountEventsClient.TokenWait, h.Clock.Waits);

        h.Server.RefuseConnect = null;
        h.Token = "token-2";
        h.Client.TokenChanged();
        await h.Until(() => h.Client.State == AccountEventsState.Live && h.Server.Tokens.Last() == "token-2", "reconnected");
    }

    [Fact]
    public async Task A_new_session_token_is_sent_on_the_open_socket()
    {
        await using var h = new Harness();
        h.Client.Start();
        await h.Until(() => h.Client.State == AccountEventsState.Live, "socket");
        var socket = h.Server.Open!;
        await h.Until(() => socket.Sent.Contains("""{"type":"ping"}"""), "a ping");
        h.Token = "token-2";
        h.Client.TokenChanged();
        await h.Until(() => socket.Sent.Any(m => m.Contains("refresh")), "the refresh");
        var refresh = JsonNode.Parse(socket.Sent.First(m => m.Contains("refresh")))!.AsObject();
        Assert.Equal("refresh", refresh["type"]!.GetValue<string>());
        Assert.Equal("token-2", refresh["token"]!.GetValue<string>());
        Assert.Equal(2, refresh.Count);
        Assert.Single(h.Server.Connects);
    }

    [Fact]
    public async Task A_refresh_the_object_could_not_check_is_sent_again_at_the_next_tick()
    {
        await using var h = new Harness();
        h.Client.Start();
        await h.Until(() => h.Client.State == AccountEventsState.Live, "socket");
        var socket = h.Server.Open!;
        h.Token = "token-2";
        h.Client.TokenChanged();
        await h.Until(() => socket.Sent.Count(m => m.Contains("refresh")) == 1, "the refresh");
        socket.Push("""{"type":"refresh_failed","retry":true}""");
        await h.Until(() => socket.Sent.Count(m => m.Contains("refresh")) == 2, "the refresh again");
        Assert.Equal(2, socket.Sent.Count(m => m.Contains("token-2")));
        await Task.Delay(100);
        var pings = socket.Sent.Count(m => m.Contains("ping"));
        await h.Until(() => socket.Sent.Count(m => m.Contains("ping")) > pings, "back to pings");
        Assert.Equal(2, socket.Sent.Count(m => m.Contains("refresh")));
    }

    [Fact]
    public async Task With_sockets_blocked_the_poll_keeps_it_current_and_the_socket_is_tried_again_later()
    {
        await using var h = new Harness();
        h.Server.BlockSockets = true;
        h.Client.Start();
        await h.Until(() => h.Client.State == AccountEventsState.Polling, "polling");
        h.Server.PublishQuietly("credits", "a");
        h.Server.PublishQuietly("subscription", "b");
        await h.Until(() => h.Kinds.Count() == 2, "polled events");
        Assert.Equal(["credits", "subscription"], h.Kinds);
        Assert.Contains(h.Server.Polls, p => p.Query.Contains("after=0"));
        Assert.All(h.Clock.Waits.Where(w => w >= AccountEventsClient.PollInterval && w < AccountEventsClient.TokenWait),
            w => Assert.InRange(w.TotalSeconds, 10, 12));

        var connectsWhilePolling = h.Server.Connects.Count;
        h.Server.BlockSockets = false;
        await h.Until(() => h.Client.State == AccountEventsState.Live, "back on the socket");
        var elapsed = h.Clock.Waits.Sum(w => w.TotalSeconds);
        Assert.True(elapsed >= AccountEventsClient.SocketRetryWhilePolling.TotalSeconds, $"retried after {elapsed} s");
        Assert.Equal(connectsWhilePolling + 1, h.Server.Connects.Count);
        h.Server.Publish("job", "c");
        await h.Until(() => h.Kinds.Count() == 3, "live event");
    }

    [Fact]
    public async Task A_network_without_sockets_or_polls_keeps_retrying_and_never_gives_up()
    {
        await using var h = new Harness();
        h.Server.BlockSockets = true;
        h.Server.RefusePoll = new AccountEventsRefusedException(null, null, null);
        h.Client.Start();
        await h.Until(() => h.Server.Connects.Count >= 5, "five attempts");
        Assert.NotEqual(AccountEventsState.Suspended, h.Client.State);
        h.Server.BlockSockets = false;
        h.Server.RefusePoll = null;
        await h.Until(() => h.Client.State == AccountEventsState.Live, "recovered");
    }

    [Fact]
    public async Task Busy_answers_back_off_and_honour_retry_after()
    {
        await using var h = new Harness();
        h.Server.RefuseConnect = new AccountEventsRefusedException(429, "rate_limited", TimeSpan.FromSeconds(60));
        h.Client.Start();
        await h.Until(() => h.Server.Connects.Count >= 3, "three attempts");
        Assert.Contains(h.Clock.Waits, w => w == TimeSpan.FromSeconds(60));
        Assert.Empty(h.Server.Polls);
    }

    [Fact]
    public async Task Reconnects_back_off_exponentially_up_to_a_minute()
    {
        await using var h = new Harness();
        h.Server.RefuseConnect = new AccountEventsRefusedException(503, "dependency_unavailable", null);
        h.Client.Start();
        await h.Until(() => h.Server.Connects.Count >= 9, "nine attempts");
        var waits = h.Clock.Waits.Take(8).Select(w => w.TotalSeconds).ToList();
        for (var i = 1; i < waits.Count; i++)
            Assert.True(waits[i] >= waits[i - 1] * 0.6, $"{waits[i - 1]} then {waits[i]}");
        Assert.All(waits, w => Assert.InRange(w, 0.8, 72));
        Assert.InRange(waits[^1], 48, 72);
    }

    [Fact]
    public async Task Without_a_session_it_waits_and_connects_once_signed_in()
    {
        await using var h = new Harness { Token = null };
        h.Client.Start();
        await h.Until(() => h.Client.State == AccountEventsState.WaitingForSignIn, "waiting");
        Assert.Empty(h.Server.Connects);
        h.Token = "token-1";
        h.Client.TokenChanged();
        await h.Until(() => h.Client.State == AccountEventsState.Live, "live");
    }

    [Fact]
    public async Task Job_events_count_as_live_only_on_a_live_socket_that_says_so()
    {
        await using var h = new Harness();
        h.Server.Jobs = true;
        h.Client.Start();
        await h.Until(() => h.Client.State == AccountEventsState.Live, "socket");
        Assert.True(h.Client.JobEventsLive);
        h.Server.RefuseConnect = new AccountEventsRefusedException(503, null, null);
        h.Server.Open!.Close(1006);
        await h.Until(() => h.Client.State == AccountEventsState.Connecting, "reconnecting");
        Assert.False(h.Client.JobEventsLive);
    }

    [Fact]
    public async Task A_subscriber_that_throws_never_stops_the_connection()
    {
        await using var h = new Harness();
        h.Client.Tickled += _ => throw new InvalidOperationException("subscriber bug");
        h.Client.Start();
        await h.Until(() => h.Client.State == AccountEventsState.Live, "socket");
        h.Server.Publish("credits", "a");
        h.Server.Publish("credits", "b");
        await h.Until(() => h.Kinds.Count() == 2, "both events");
    }

    [Fact]
    public async Task A_receive_loop_that_falls_behind_is_not_a_silent_socket_on_the_virtual_clock()
    {
        // A busy machine delays the client's reads; virtual time must wait for them, or the silence check drops a
        // healthy socket and the reconnect's hello merges what it replays into one tickle.
        await using var h = new Harness();
        h.Client.Tickled += tickle =>
        {
            if (!tickle.Reset)
                Thread.Sleep(50);
        };
        h.Client.Start();
        await h.Until(() => h.Client.State == AccountEventsState.Live, "socket");
        h.Server.Publish("credits", "a");
        h.Server.Publish("credits", "b");
        await h.Until(() => h.Kinds.Count() == 2, "both events");
        Assert.Single(h.Server.Connects);
    }

    [Fact]
    public void Tickles_say_what_to_re_read_and_treat_an_unknown_kind_as_everything()
    {
        AccountEventsTickle Of(params string[] kinds) => new(new HashSet<string>(kinds), false);
        Assert.True(Of("credits").Credits);
        Assert.False(Of("credits").Jobs);
        Assert.True(Of("job").Jobs);
        Assert.False(Of("job").Credits);
        Assert.True(Of("chat").Credits);
        Assert.True(Of("subscription").Credits);
        Assert.True(Of("newkind").Credits && Of("newkind").Jobs);
        Assert.True(new AccountEventsTickle(new HashSet<string>(), true) is { Credits: true, Jobs: true });
    }

    [Fact]
    public async Task Stop_ends_the_loop_and_closes_the_socket()
    {
        var h = new Harness();
        h.Client.Start();
        await h.Until(() => h.Client.State == AccountEventsState.Live, "socket");
        var socket = h.Server.Open!;
        await h.Client.StopAsync();
        Assert.Equal(AccountEventsState.Stopped, h.Client.State);
        Assert.True(socket.Closed);
    }
}
