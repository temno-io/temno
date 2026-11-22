using System.Globalization;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Temno.Core.Account;

/// <summary>
/// What changed on the account since the last tickle. <see cref="Reset"/> means the client can't tell (a gap, a new
/// epoch, a first connection), so everything is re-read. Postgres stays the truth; this only says what to re-read.
/// </summary>
public sealed record AccountEventsTickle(IReadOnlySet<string> Kinds, bool Reset)
{
    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        AccountEventKinds.Credits, AccountEventKinds.Subscription, AccountEventKinds.Job, AccountEventKinds.Chat,
    };

    /// <summary>The balance or plan may have changed. A kind this build doesn't know counts as everything.</summary>
    public bool Credits => Reset || Kinds.Any(kind => kind is AccountEventKinds.Credits or AccountEventKinds.Subscription
        or AccountEventKinds.Chat || !Known.Contains(kind));

    /// <summary>A hosted job may have changed.</summary>
    public bool Jobs => Reset || Kinds.Any(kind => kind == AccountEventKinds.Job || !Known.Contains(kind));
}

public enum AccountEventsState
{
    Stopped,
    WaitingForSignIn,
    Connecting,
    Live,

    /// <summary>The socket can't be opened (a proxy refuses WebSockets); the edge's short poll keeps the app current.</summary>
    Polling,

    /// <summary>The account is suspended. The client stopped and never retries (ADR-007 §7).</summary>
    Suspended,
}

/// <summary>The events Worker refused a socket or a poll, with its status and <c>X-Temno-Error</c> when it answered.</summary>
public sealed class AccountEventsRefusedException : Exception
{
    public AccountEventsRefusedException(int? status, string? error, TimeSpan? retryAfter, Exception? inner = null)
        : base($"Account events refused ({status?.ToString(CultureInfo.InvariantCulture) ?? "no answer"}).", inner)
    {
        Status = status;
        Error = error;
        RetryAfter = retryAfter;
    }

    public int? Status { get; }
    public string? Error { get; }
    public TimeSpan? RetryAfter { get; }
    public bool Suspended => Status == 403 && Error == "account_suspended";
}

public interface IAccountEventsSocket : IAsyncDisposable
{
    Task SendAsync(string text, CancellationToken cancellationToken);

    /// <summary>The next text message, or null once the socket closed; <see cref="CloseCode"/> then says why.</summary>
    Task<string?> ReceiveAsync(CancellationToken cancellationToken);

    int? CloseCode { get; }
}

public interface IAccountEventsTransport
{
    /// <summary>Opens the socket. Throws <see cref="AccountEventsRefusedException"/> when it can't.</summary>
    Task<IAccountEventsSocket> ConnectAsync(Uri url, string token, CancellationToken cancellationToken);

    /// <summary>The fallback poll's JSON. Throws <see cref="AccountEventsRefusedException"/> for anything but 200.</summary>
    Task<string> PollAsync(Uri url, string token, CancellationToken cancellationToken);
}

/// <summary>
/// One connection per running Temno and account (docs/contracts/account-events.md). It resumes from the last
/// <c>seq</c> after a drop, refreshes the socket's token when the session's changes, falls back to polling when the
/// socket can't be opened, and stops for good when the account is suspended. Events are raised on its own thread.
/// </summary>
public sealed class AccountEventsClient : IAsyncDisposable
{
    public const string Path = "/v1/events";
    public static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(25);

    /// <summary>Nothing heard for this long (a pong comes back to every ping) means a half-open connection.</summary>
    public static readonly TimeSpan Silence = TimeSpan.FromSeconds(70);
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan SocketRetryWhilePolling = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan TokenWait = TimeSpan.FromSeconds(60);

    private readonly Uri _baseUrl;
    private readonly Func<string?> _token;
    private readonly IAccountEventsTransport _transport;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Random _random;
    private readonly object _gate = new();
    private CancellationTokenSource _wake = new();
    private CancellationTokenSource? _run;
    private Task? _loop;
    private AccountEventsState _state;
    private bool _jobs;
    private string? _epoch;
    private long _seq;

    public AccountEventsClient(
        Uri baseUrl,
        Func<string?> token,
        IAccountEventsTransport transport,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<DateTimeOffset>? clock = null,
        Random? random = null)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        _baseUrl = baseUrl;
        _token = token ?? throw new ArgumentNullException(nameof(token));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _delay = delay ?? ((gap, ct) => Task.Delay(gap, ct));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _random = random ?? Random.Shared;
    }

    public event Action<AccountEventsTickle>? Tickled;
    public event Action<AccountEventsState>? StateChanged;

    public AccountEventsState State
    {
        get { lock (_gate) return _state; }
    }

    /// <summary>The socket is live and the server says job events are published (from WP-348 on).</summary>
    public bool JobEventsLive
    {
        get { lock (_gate) return _state == AccountEventsState.Live && _jobs; }
    }

    public string? Epoch => Volatile.Read(ref _epoch);
    public long Seq => Interlocked.Read(ref _seq);

    public void Start()
    {
        lock (_gate)
        {
            if (_run is not null)
                return;
            _run = new CancellationTokenSource();
            var token = _run.Token;
            _loop = Task.Run(() => RunAsync(token));
        }
    }

    public async Task StopAsync()
    {
        Task? loop;
        lock (_gate)
        {
            try { _run?.Cancel(); } catch (ObjectDisposedException) { }
            loop = _loop;
        }

        if (loop is not null)
        {
            try { await loop.ConfigureAwait(false); }
            catch (Exception) { /* stopping */ }
        }

        SetState(AccountEventsState.Stopped);
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    /// <summary>The session's access token changed: refresh the open socket, or retry a refused one now.</summary>
    public void TokenChanged()
    {
        lock (_gate)
        {
            try { _wake.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (!await StepAsync(ct).ConfigureAwait(false))
                        return;
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                    // Nothing expected reaches here; whatever did, the connection is retried, never abandoned.
                    await SleepAsync(Backoff(++_attempt), ct).ConfigureAwait(false);
                }
            }
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // Stopped with the session, whatever was in flight.
        }
    }

    private int _attempt;
    private DateTimeOffset _pollUntil = DateTimeOffset.MinValue;

    /// <summary>One connection, poll or wait. False when the account is suspended and the client stops for good.</summary>
    private async Task<bool> StepAsync(CancellationToken ct)
    {
        var token = _token();
        if (string.IsNullOrEmpty(token))
        {
            SetState(AccountEventsState.WaitingForSignIn);
            await SleepAsync(TokenWait, ct).ConfigureAwait(false);
            return true;
        }

        if (_clock() < _pollUntil)
            return await PollOnceAsync(token, ct).ConfigureAwait(false);

        SetState(AccountEventsState.Connecting);
        IAccountEventsSocket socket;
        try
        {
            socket = await _transport.ConnectAsync(Url(), token, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            var refused = ex as AccountEventsRefusedException;
            if (refused?.Suspended == true)
            {
                Suspend();
                return false;
            }

            if (refused?.Status == 401)
            {
                await SleepAsync(TokenWait, ct).ConfigureAwait(false);
                return true;
            }

            if (refused?.Status is 429 or 503)
            {
                await SleepAsync(Longer(refused.RetryAfter, Backoff(++_attempt)), ct).ConfigureAwait(false);
                return true;
            }

            // Anything else, a proxy that refuses WebSockets included: one poll tells the socket from the network.
            // While polls work they carry the app, and the socket is tried again later. A poll that fails has waited.
            if (!await PollOnceAsync(token, ct).ConfigureAwait(false))
                return false;
            if (State == AccountEventsState.Polling)
                _pollUntil = _clock() + SocketRetryWhilePolling;
            return true;
        }

        _pollUntil = DateTimeOffset.MinValue;
        var closed = await RunSocketAsync(socket, token, ct).ConfigureAwait(false);
        if (closed == AccountEventCloseCodes.AccountSuspended)
        {
            Suspend();
            return false;
        }

        SetState(AccountEventsState.Connecting);
        var wait = closed switch
        {
            AccountEventCloseCodes.AuthenticationRequired => TokenWait,
            AccountEventCloseCodes.Replaced => MaxBackoff,
            _ => Backoff(++_attempt),
        };
        await SleepAsync(wait, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>One poll. False only when the account is suspended and the client must stop.</summary>
    private async Task<bool> PollOnceAsync(string token, CancellationToken ct)
    {
        try
        {
            var json = await _transport.PollAsync(Url(), token, ct).ConfigureAwait(false);
            Apply(ParseSnapshot(json));
            SetState(AccountEventsState.Polling);
            await SleepAsync(PollInterval + TimeSpan.FromMilliseconds(_random.Next(0, 2000)), ct).ConfigureAwait(false);
            return true;
        }
        catch (AccountEventsRefusedException refused) when (refused.Suspended)
        {
            Suspend();
            return false;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            var refused = ex as AccountEventsRefusedException;
            SetState(AccountEventsState.Connecting);
            var wait = refused?.Status == 401 ? TokenWait : Longer(refused?.RetryAfter, PollInterval);
            await SleepAsync(wait, ct).ConfigureAwait(false);
            return true;
        }
    }

    /// <summary>Runs one socket until it closes, and returns the server's close code (null for a dropped connection).</summary>
    private async Task<int?> RunSocketAsync(IAccountEventsSocket socket, string token, CancellationToken ct)
    {
        await using var _ = socket.ConfigureAwait(false);
        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heard = new Heard { Ticks = _clock().UtcTicks };
        var keepAlive = KeepAliveAsync(socket, token, heard, session);
        try
        {
            while (await socket.ReceiveAsync(session.Token).ConfigureAwait(false) is { } text)
            {
                Volatile.Write(ref heard.Ticks, _clock().UtcTicks);
                // Supabase Auth was down when the object checked the new token: send it again at the next tick.
                if (Handle(text) == "refresh_failed")
                    Volatile.Write(ref heard.Resend, 1);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException
            || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            // The connection dropped; the cursor makes the next socket replay what was missed.
        }
        finally
        {
            session.Cancel();
            try { await keepAlive.ConfigureAwait(false); } catch (Exception) { /* the socket is gone */ }
        }

        return socket.CloseCode;
    }

    private sealed class Heard
    {
        public long Ticks;
        public int Resend;
    }

    /// <summary>
    /// Pings, and sends the session's new token whenever it changed. A socket that stayed silent past
    /// <see cref="Silence"/>, or that can't be written to, is dropped, so a half-open connection reconnects.
    /// </summary>
    private async Task KeepAliveAsync(IAccountEventsSocket socket, string token, Heard heard, CancellationTokenSource session)
    {
        var ct = session.Token;
        var sent = token;
        while (!ct.IsCancellationRequested)
        {
            await SleepAsync(PingInterval, ct).ConfigureAwait(false);
            if (_clock() - new DateTimeOffset(Volatile.Read(ref heard.Ticks), TimeSpan.Zero) > Silence)
            {
                session.Cancel();
                return;
            }

            var current = _token();
            try
            {
                if (!string.IsNullOrEmpty(current) && (current != sent || Interlocked.Exchange(ref heard.Resend, 0) == 1))
                {
                    await socket.SendAsync(JsonSerializer.Serialize(new { type = "refresh", token = current }), ct).ConfigureAwait(false);
                    sent = current;
                }
                else
                {
                    await socket.SendAsync("{\"type\":\"ping\"}", ct).ConfigureAwait(false);
                }
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                session.Cancel();
                return;
            }
        }
    }

    /// <summary>
    /// Applies one message and returns its type. A message the client can't read is skipped: the next hello, poll
    /// or gap re-reads everything.
    /// </summary>
    private string? Handle(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type))
                return null;
            var name = type.GetString();
            switch (name)
            {
                case "hello":
                    Apply(ParseSnapshot(root));
                    // Only a socket that got as far as its hello resets the backoff, so a flapping one can't spin.
                    _attempt = 0;
                    SetState(AccountEventsState.Live);
                    break;
                case "event" when ReadEvent(root) is { } item:
                    ApplyEvent(item.Seq, item.Kind);
                    break;
            }

            return name;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private void Apply(Snapshot snapshot)
    {
        lock (_gate)
            _jobs = snapshot.Jobs;
        if (snapshot.Reset || snapshot.Epoch != Epoch)
        {
            Volatile.Write(ref _epoch, snapshot.Epoch);
            Interlocked.Exchange(ref _seq, snapshot.Seq);
            Raise(new AccountEventsTickle(new HashSet<string>(), Reset: true));
            return;
        }

        var kinds = new HashSet<string>(StringComparer.Ordinal);
        var gap = false;
        var seq = Seq;
        foreach (var (eventSeq, kind) in snapshot.Events.OrderBy(e => e.Seq))
        {
            if (eventSeq <= seq)
                continue;
            gap |= eventSeq != seq + 1;
            seq = eventSeq;
            kinds.Add(kind);
        }

        gap |= snapshot.Seq > seq;
        Interlocked.Exchange(ref _seq, Math.Max(seq, snapshot.Seq));
        if (gap)
            Raise(new AccountEventsTickle(new HashSet<string>(), Reset: true));
        else if (kinds.Count > 0)
            Raise(new AccountEventsTickle(kinds, Reset: false));
    }

    private void ApplyEvent(long seq, string kind)
    {
        var last = Seq;
        if (Epoch is null || seq <= last)
            return;
        Interlocked.Exchange(ref _seq, seq);
        Raise(seq == last + 1
            ? new AccountEventsTickle(new HashSet<string>(StringComparer.Ordinal) { kind }, Reset: false)
            : new AccountEventsTickle(new HashSet<string>(), Reset: true));
    }

    private void Raise(AccountEventsTickle tickle)
    {
        try { Tickled?.Invoke(tickle); }
        catch (Exception) { /* a subscriber's failure never stops the connection */ }
    }

    private void Suspend() => SetState(AccountEventsState.Suspended);

    private void SetState(AccountEventsState state)
    {
        lock (_gate)
        {
            if (_state == state || _state == AccountEventsState.Suspended && state != AccountEventsState.Stopped)
                return;
            _state = state;
        }

        try { StateChanged?.Invoke(state); }
        catch (Exception) { /* as above */ }
    }

    private Uri Url()
    {
        var epoch = Epoch;
        var query = epoch is null ? "" : $"?after={Seq.ToString(CultureInfo.InvariantCulture)}&epoch={epoch}";
        return new Uri(_baseUrl, Path + query);
    }

    /// <summary>Waits for <paramref name="gap"/>, or less when the token changes.</summary>
    private async Task SleepAsync(TimeSpan gap, CancellationToken ct)
    {
        CancellationTokenSource wake;
        lock (_gate)
            wake = _wake;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, wake.Token);
        try
        {
            await _delay(gap, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_wake, wake))
                    _wake = new CancellationTokenSource();
            }
        }
    }

    private TimeSpan Backoff(int attempt)
    {
        var seconds = Math.Min(MaxBackoff.TotalSeconds, Math.Pow(2, Math.Clamp(attempt - 1, 0, 6)));
        return TimeSpan.FromSeconds(seconds * (0.8 + _random.NextDouble() * 0.4));
    }

    private static TimeSpan Longer(TimeSpan? a, TimeSpan b) => a is { } value && value > b ? value : b;

    private sealed record Snapshot(string Epoch, long Seq, bool Reset, bool Jobs, IReadOnlyList<(long Seq, string Kind)> Events);

    private static Snapshot ParseSnapshot(string json)
    {
        using var document = JsonDocument.Parse(json);
        return ParseSnapshot(document.RootElement);
    }

    private static Snapshot ParseSnapshot(JsonElement root)
    {
        var events = new List<(long, string)>();
        if (root.TryGetProperty("events", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                if (ReadEvent(item) is { } e)
                    events.Add((e.Seq, e.Kind));
            }
        }

        return new Snapshot(
            root.GetProperty("epoch").GetString() ?? throw new JsonException("epoch"),
            root.GetProperty("seq").GetInt64(),
            root.TryGetProperty("reset", out var reset) && reset.ValueKind == JsonValueKind.True,
            root.TryGetProperty("jobs", out var jobs) && jobs.ValueKind == JsonValueKind.True,
            events);
    }

    private static (long Seq, string Kind)? ReadEvent(JsonElement item)
        => item.ValueKind == JsonValueKind.Object
           && item.TryGetProperty("seq", out var seq) && seq.TryGetInt64(out var value)
           && item.TryGetProperty("kind", out var kind) && kind.GetString() is { Length: > 0 } name
            ? (value, name)
            : null;
}

/// <summary>The real transport: <see cref="ClientWebSocket"/> with the session's token in the upgrade request.</summary>
public sealed class ClientWebSocketTransport : IAccountEventsTransport
{
    public const int MaxMessageBytes = 512 * 1024;
    private readonly string _environment;
    private readonly HttpClient _http;

    public ClientWebSocketTransport(string environment, HttpClient? http = null)
    {
        _environment = environment;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    public async Task<IAccountEventsSocket> ConnectAsync(Uri url, string token, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + token);
        socket.Options.SetRequestHeader(Temno.Environments.TemnoEnvironments.HeaderName, _environment);
        socket.Options.CollectHttpResponseDetails = true;
        // The client pings by message every 25 s; the Worker answers those without waking the account's object.
        socket.Options.KeepAliveInterval = TimeSpan.Zero;
        var builder = new UriBuilder(url) { Scheme = url.Scheme == Uri.UriSchemeHttps ? "wss" : "ws" };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            await socket.ConnectAsync(builder.Uri, timeout.Token).ConfigureAwait(false);
            return new Socket(socket);
        }
        catch (Exception ex) when (ex is WebSocketException or HttpRequestException
            || ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            var status = (int)socket.HttpStatusCode;
            var headers = socket.HttpResponseHeaders;
            socket.Dispose();
            throw new AccountEventsRefusedException(status == 0 ? null : status, Header(headers, "X-Temno-Error"),
                RetryAfter(Header(headers, "Retry-After")), ex);
        }
    }

    public async Task<string> PollAsync(Uri url, string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation(Temno.Environments.TemnoEnvironments.HeaderName, _environment);
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode != 200)
            {
                var headers = response.Headers.ToDictionary(h => h.Key, h => h.Value, StringComparer.OrdinalIgnoreCase);
                throw new AccountEventsRefusedException((int)response.StatusCode, Header(headers, "X-Temno-Error"),
                    RetryAfter(Header(headers, "Retry-After")));
            }

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var _ = stream.ConfigureAwait(false);
            using var bounded = new MemoryStream();
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (bounded.Length + read > MaxMessageBytes)
                    throw new AccountEventsRefusedException(null, "too_large", null);
                bounded.Write(buffer, 0, read);
            }

            return Encoding.UTF8.GetString(bounded.ToArray());
        }
        catch (HttpRequestException ex)
        {
            throw new AccountEventsRefusedException(null, null, null, ex);
        }
    }

    private static string? Header(IReadOnlyDictionary<string, IEnumerable<string>>? headers, string name)
        => headers is not null && headers.TryGetValue(name, out var values) ? values.FirstOrDefault() : null;

    private static TimeSpan? RetryAfter(string? value)
        => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds is > 0 and <= 3600
            ? TimeSpan.FromSeconds(seconds)
            : null;

    private sealed class Socket(ClientWebSocket socket) : IAccountEventsSocket
    {
        private readonly SemaphoreSlim _send = new(1, 1);

        public int? CloseCode { get; private set; }

        public async Task SendAsync(string text, CancellationToken cancellationToken)
        {
            await _send.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _send.Release();
            }
        }

        public async Task<string?> ReceiveAsync(CancellationToken cancellationToken)
        {
            var buffer = new byte[16 * 1024];
            using var message = new MemoryStream();
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    CloseCode = (int?)socket.CloseStatus;
                    return null;
                }

                if (result.MessageType != WebSocketMessageType.Text || message.Length + result.Count > MaxMessageBytes)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.InvalidPayloadData, "invalid_message", cancellationToken).ConfigureAwait(false);
                    return null;
                }

                message.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                    return Encoding.UTF8.GetString(message.ToArray());
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (socket.State == WebSocketState.Open)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "closing", timeout.Token).ConfigureAwait(false); }
                catch (Exception) { /* closing anyway */ }
            }

            socket.Dispose();
            _send.Dispose();
        }
    }
}
