using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Temno.Core.Account;
using Temno.Core.Agent;
using Temno.Core.Transcription;
using Temno.Generation;
using Xunit;

namespace Temno.Core.Tests;

public sealed class HostedAccountTests
{
    [Fact]
    public void Pkce_ChallengeIsS256Base64Url()
    {
        var verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", Pkce.ChallengeS256(verifier));
    }

    [Fact]
    public void HostedEndpoints_RejectsRedirectWithQuery()
    {
        Assert.True(HostedEndpoints.TryParseLoopbackRedirect("http://127.0.0.1:17321/callback", out var port, out var path));
        Assert.Equal(17321, port);
        Assert.Equal("/callback", path);
        Assert.False(HostedEndpoints.TryParseLoopbackRedirect("http://127.0.0.1:17321/callback?x=1", out _, out _));
        Assert.False(HostedEndpoints.TryParseLoopbackRedirect("https://example.test/callback", out _, out _));
    }

    [Fact]
    public async Task GoogleSignInFlow_ReplayAndExpiryAreRejected()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue("""{"access_token":"a","refresh_token":"r","user":{"id":"user","email":"t@example.com"}}""");
        using var http = new HttpClient(handler);
        var auth = new SupabaseAuthClient("https://example.test", "anon", http);
        var flow = new GoogleSignInFlow();
        flow.Begin("http://127.0.0.1:17321/callback", TimeSpan.FromMinutes(1));
        var first = await flow.CompleteAsync(new CallbackResult(CallbackKind.Code, "abc", null), auth);
        Assert.Equal(SignInOutcomeKind.Exchanged, first.Kind);

        var replay = await flow.CompleteAsync(new CallbackResult(CallbackKind.Code, "abc", null), auth);
        Assert.Equal(SignInOutcomeKind.Replay, replay.Kind);

        var expired = new GoogleSignInFlow();
        expired.Begin("http://127.0.0.1:17321/callback", TimeSpan.FromMilliseconds(1));
        await Task.Delay(20);
        var late = await expired.CompleteAsync(new CallbackResult(CallbackKind.Code, "abc", null), auth);
        Assert.Equal(SignInOutcomeKind.Expired, late.Kind);
    }

    [Fact]
    public async Task LoopbackCallback_SpendsFirstMatchingGetWithoutQueryLogging()
    {
        using var server = new LoopbackCallbackServer(0);
        var wait = server.WaitAsync("/callback", TimeSpan.FromSeconds(5));
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, server.Port);
        var payload = "GET /callback?code=from-google HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n";
        await tcp.GetStream().WriteAsync(Encoding.ASCII.GetBytes(payload));
        var result = await wait;
        Assert.Equal(CallbackKind.Code, result.Kind);
        Assert.Equal("from-google", result.Code);
    }

    [Fact]
    public async Task CreditBalance_ParsesSqlShapeAndNullMeansNoAccount()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue("null");
        handler.Enqueue("""{"total":40,"reserved":5,"available":35}""");
        using var http = new HttpClient(handler);
        var client = new CreditBalanceClient("https://example.test", "anon", http);
        Assert.Null(await client.ReadAsync("token"));
        var balance = await client.ReadAsync("token");
        Assert.Equal(new CreditBalance(40, 5, 35), balance);
    }

    [Fact]
    public async Task CreditBalance_ParsesNextExpiry()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue("""{"total":250,"reserved":100,"available":150,"ownerTesting":false,"nextExpiry":{"credits":150,"at":"2026-12-28T03:40:12.123456+00:00"}}""");
        handler.Enqueue("""{"total":50,"reserved":0,"available":50,"ownerTesting":false,"nextExpiry":null}""");
        handler.Enqueue("""{"total":50,"reserved":0,"available":50,"nextExpiry":{"credits":"150","at":"2026-12-28T03:40:12Z"}}""");
        handler.Enqueue("""{"total":50,"reserved":0,"available":50,"nextExpiry":{"credits":150,"at":"not a date"}}""");
        using var http = new HttpClient(handler);
        var client = new CreditBalanceClient("https://example.test", "anon", http);

        var expiring = (await client.ReadAsync("token"))!.Value;
        Assert.Equal(150, expiring.NextExpiry?.Credits);
        Assert.Equal(new DateTimeOffset(2026, 12, 28, 3, 40, 12, TimeSpan.Zero).AddTicks(1234560), expiring.NextExpiry?.At);
        Assert.Equal(150, expiring.Available);

        Assert.Null((await client.ReadAsync("token"))!.Value.NextExpiry);
        Assert.Null((await client.ReadAsync("token"))!.Value.NextExpiry);
        Assert.Null((await client.ReadAsync("token"))!.Value.NextExpiry);
    }

    [Fact]
    public void HostedApiErrors_402And503AreDistinct()
    {
        Assert.Equal("Insufficient credits. Open Account and choose a plan.", HostedApiErrors.Describe(402, "insufficient_credits", null));
        Assert.Equal("The hosted provider is down.", HostedApiErrors.Describe(503, "dependency_unavailable", null));
        Assert.True(new HostedApiException("x", 402, "insufficient_credits").IsInsufficientCredits);
        Assert.True(new HostedApiException("x", 503, "dependency_unavailable").IsProviderDown);
        Assert.False(new HostedApiException("x", 402, "insufficient_credits").IsProviderDown);
    }

    [Fact]
    public async Task HostedGeneration_402DoesNotLookLikeProviderDown()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue("""{"error":"insufficient_credits"}""", HttpStatusCode.PaymentRequired);
        using var http = new HttpClient(handler);
        var client = new HostedGenerationClient("https://gateway.example.test", () => "token", http);
        var ex = await Assert.ThrowsAsync<GenerationException>(() =>
            client.GetJobStatusAsync("job-1"));
        Assert.Equal(402, ex.StatusCode);
        Assert.Equal("insufficient_credits", ex.Code);
        Assert.Contains("Insufficient credits", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("provider is down", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HostedGeneration_503IsProviderDown()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue("""{"error":"dependency_unavailable"}""", HttpStatusCode.ServiceUnavailable);
        using var http = new HttpClient(handler);
        var client = new HostedGenerationClient("https://gateway.example.test", () => "token", http);
        var ex = await Assert.ThrowsAsync<GenerationException>(() =>
            client.GetJobStatusAsync("job-1"));
        Assert.Equal(503, ex.StatusCode);
        Assert.Contains("provider is down", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HostedTranscription_LanguageNormalizesEnEsOnly()
    {
        Assert.Equal("en", HostedTranscriptionClient.NormalizeLanguage("en-US"));
        Assert.Equal("es", HostedTranscriptionClient.NormalizeLanguage("es-MX"));
        var ex = Assert.Throws<HostedApiException>(() => HostedTranscriptionClient.NormalizeLanguage("fr"));
        Assert.Equal("invalid_transcription_request", ex.Code);
    }

    // WP-393: the desktop refuses past the hosted 30-minute cap before any upload, with the Gateway's own message.
    [Fact]
    public void Pcm16Wav_RefusesPastThirtyMinutesWithASplitMessage()
    {
        Assert.Equal(1_800, Pcm16Wav.MaxSeconds);
        var atCap = Pcm16Wav.FromMonoFloats(new float[16_000 * 1_800], 16_000);
        Assert.Equal(1_800, atCap.DurationSeconds);
        Assert.True(atCap.Bytes.Length <= Pcm16Wav.MaxBytes);
        var refused = Assert.Throws<InvalidOperationException>(() => Pcm16Wav.FromMonoFloats(new float[16_000 * 1_800 + 1], 16_000));
        Assert.Equal(Pcm16Wav.TooLongMessage, refused.Message);
        Assert.Contains("Split longer audio into parts of 30 minutes or less", refused.Message);
        Assert.Throws<InvalidOperationException>(() => Pcm16Wav.FromMonoFloats(new float[24_000 * 1_800 + 1], 24_000));
        Assert.Equal(Pcm16Wav.TooLongMessage, HostedApiErrors.Describe(null, "audio_too_long", null));
        Assert.Contains("Nothing was charged", HostedApiErrors.Describe(null, "claim_timeout", null));
    }

    [Fact]
    public void Pcm16Wav_WrapsMonoFloats()
    {
        var wav = Pcm16Wav.FromMonoFloats([0f, 0.5f, -0.5f], 16_000);
        Assert.Equal(16_000, wav.SampleRate);
        Assert.True(wav.Bytes.Length > 44);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav.Bytes, 0, 4));
    }

    [Fact]
    public async Task HostedAgent_DoesNotRerunPersistedToolIds()
    {
        var handler = new ScriptedHandler();
        var quoteId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        handler.EnqueueSse(
            "event: tool_request\n" +
            "id: 1\n" +
            "data: [{\"id\":\"call_1\",\"type\":\"function\",\"function\":{\"name\":\"get_timeline\",\"arguments\":\"{}\"}}]\n" +
            "\n" +
            "event: terminal\n" +
            "id: 2\n" +
            "data: {\"status\":\"succeeded\",\"charge\":5}\n" +
            "\n");
        handler.Enqueue("{}");
        using var http = new HttpClient(handler);
        var client = new HostedAgentClient("https://gateway.example.test", () => "token", http, accountId: "fixture-owner");
        var conversation = new HostedAgentConversation();
        conversation.Session.Hosted!.ExecutedToolIds.Add("call_1");
        conversation.Session.Hosted.ToolResults["call_1"] = "already";
        conversation.Session.Hosted.TurnId = turnId.ToString("D");
        conversation.Session.Hosted.QuoteId = quoteId.ToString("D");
        // WP-400: only a turn this install recorded resumes on its own.
        conversation.Session.Hosted.AccountId = "fixture-owner";
        conversation.Session.Hosted.IdempotencyKey = Guid.NewGuid().ToString("D");
        HostedTurnOrigins.Record(conversation.Session.Hosted.IdempotencyKey, "fixture-owner", HostedTurnOrigins.Digest(conversation.Session.Hosted.Submission));
        HostedTurnOrigins.RecordTurn(conversation.Session.Hosted.IdempotencyKey, turnId);
        var runner = new CountingRunner();
        await conversation.ResumeAsync(
            client,
            runner,
            progress: null);

        Assert.Equal(0, runner.Calls);
        Assert.Contains(handler.Urls, u => u.Contains("/tool-results", StringComparison.Ordinal));
        Assert.Equal(5, conversation.Session.Hosted!.LastChargeCredits);
        Assert.True(conversation.Session.Hosted.Terminal);
    }

    private sealed class CountingRunner : IToolRunner
    {
        public int Calls { get; private set; }

        public Task<AgentToolExecutionResult> ExecuteAsync(string name, string inputJson, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(AgentToolExecutionResult.Ok("{}"));
        }
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _queue = new();
        public List<string> Urls { get; } = [];

        public void Enqueue(string json, HttpStatusCode status = HttpStatusCode.OK)
        {
            _queue.Enqueue(new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }

        public void EnqueueSse(string body)
        {
            _queue.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
            });
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Urls.Add(request.RequestUri?.ToString() ?? "");
            if (_queue.Count == 0)
                throw new InvalidOperationException("No scripted HTTP response remaining.");
            return Task.FromResult(_queue.Dequeue());
        }
    }
}
