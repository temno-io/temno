using System.Net;
using System.Text;
using System.Text.Json;
using Temno.Core.Transcription;
using Xunit;

namespace Temno.Core.Tests;

public sealed class HostedTranscriptionRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "temno-wp355-" + Guid.NewGuid().ToString("N"));
    private HostedTranscriptionClient Client(HttpClient http) => new("https://transcription.example.test", () => "token", http,
        new HostedTranscriptionRequestStore(_root), () => "owner-a", (gap, ct) => Task.Delay(1, ct));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
    private static readonly PreparedPcmWav Audio = Pcm16Wav.FromMonoFloats(new float[160], 16_000);

    [Fact]
    public async Task Successful_request_admits_one_job()
    {
        using var server = new TranscriptionServer();
        using var http = new HttpClient(server);
        var client = Client(http);
        Assert.Equal("recognized", (await client.TranscribeAsync(Audio, "en", null)).Text);
        Assert.Equal(1, server.Admissions);
    }

    [Fact]
    public async Task Retry_after_lost_admission_response_reuses_the_same_job()
    {
        using var server = new TranscriptionServer { LoseAdmissionResponse = true };
        using var http = new HttpClient(server);
        var client = Client(http);
        try { await client.TranscribeAsync(Audio, "en", null); }
        catch (HttpRequestException) { }
        var relaunched = Client(http);
        Assert.Equal("recognized", (await relaunched.TranscribeAsync(Audio, "en", null)).Text);
        Assert.Equal(1, server.Admissions);
    }

    [Fact]
    public async Task User_cancellation_calls_the_server_to_release_the_hold()
    {
        using var cancel = new CancellationTokenSource();
        using var server = new TranscriptionServer { CancelOnStatus = cancel };
        using var http = new HttpClient(server);
        var client = Client(http);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.TranscribeAsync(Audio, "en", null, cancel.Token));
        Assert.Equal(1, server.Cancellations);
    }

    [Theory]
    [InlineData("upload")]
    [InlineData("part")]
    [InlineData("finalize")]
    [InlineData("status")]
    [InlineData("result")]
    public async Task Lost_transport_at_each_stage_does_not_admit_twice(string phase)
    {
        using var server = new TranscriptionServer { FailPhase = phase };
        using var http = new HttpClient(server);
        Assert.Equal("recognized", (await Client(http).TranscribeAsync(Audio, "en", null)).Text);
        Assert.Equal(1, server.Admissions);
        Assert.Equal(1, server.Uploads);
        Assert.True(server.FailedOnce);
    }

    [Fact]
    public async Task Process_loss_after_admission_is_recovered_from_disk_without_audio_resubmission()
    {
        Directory.CreateDirectory(_root);
        var media = Path.Combine(_root, "source.wav");
        File.WriteAllBytes(media, Audio.Bytes);
        using var server = new TranscriptionServer { CrashAfterAdmission = true };
        using var http = new HttpClient(server);
        await Assert.ThrowsAsync<SimulatedProcessExit>(() => Client(http).TranscribeAsync(Audio, "en", null, sourcePath: media));
        var journal = File.ReadAllText(Directory.GetFiles(_root, "*.json", SearchOption.AllDirectories).Single());
        Assert.Contains("\"submitStarted\":true", journal);
        Assert.DoesNotContain("token", journal);
        await Client(http).ResumePendingAsync(new NoAudioPreparation());
        Assert.True(TranscriptDiskCache.TryLoad(media, out var recovered));
        Assert.Equal("recognized", recovered!.Text);
        Assert.Equal(1, server.Admissions);
        Assert.Equal(1, server.PartsWritten);
    }

    [Fact]
    public async Task Cancellation_after_lost_admission_uses_request_key_without_readmitting()
    {
        using var cancel = new CancellationTokenSource();
        using var server = new TranscriptionServer { LoseAdmissionResponse = true, CancelOnAdmission = cancel };
        using var http = new HttpClient(server);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(http).TranscribeAsync(Audio, "en", null, cancel.Token));
        Assert.Equal(1, server.Admissions);
        Assert.Equal(1, server.RequestCancellations);
        Assert.Equal(1, server.Cancellations);
    }

    [Fact]
    public async Task An_unacknowledged_offline_cancel_survives_relaunch()
    {
        using var cancel = new CancellationTokenSource();
        using var server = new TranscriptionServer { CancelOnStatus = cancel, RejectCancellation = true };
        using var http = new HttpClient(server);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(http).TranscribeAsync(Audio, "en", null, cancel.Token));
        var pending = new HostedTranscriptionRequestStore(_root).Pending("owner-a", "https://transcription.example.test");
        Assert.True(Assert.Single(pending).CancelRequested);
        server.RejectCancellation = false;
        await Client(http).ResumePendingAsync(new NoAudioPreparation());
        Assert.Empty(new HostedTranscriptionRequestStore(_root).Pending("owner-a", "https://transcription.example.test"));
        Assert.Equal(1, server.Admissions);
        Assert.Equal(1, server.Cancellations);
    }

    [Fact]
    public async Task Corrupt_recovery_metadata_never_mints_fresh_keys()
    {
        using var server = new TranscriptionServer();
        using var http = new HttpClient(server);
        await Client(http).TranscribeAsync(Audio, "en", null);
        File.WriteAllText(Directory.GetFiles(_root, "*.json", SearchOption.AllDirectories).Single(), "{}");
        var error = await Assert.ThrowsAsync<Temno.Core.Account.HostedApiException>(() => Client(http).TranscribeAsync(Audio, "en", null));
        Assert.Equal("transcription_recovery_invalid", error.Code);
        Assert.Equal(1, server.Admissions);
    }

    [Fact]
    public async Task Account_switch_stops_polling_but_preserves_the_original_request()
    {
        var account = "owner-a";
        using var server = new TranscriptionServer { OnStatus = () => account = "owner-b" };
        using var http = new HttpClient(server);
        var client = new HostedTranscriptionClient("https://transcription.example.test", () => "token", http,
            new HostedTranscriptionRequestStore(_root), () => account, (gap, ct) => Task.Delay(1, ct));
        var error = await Assert.ThrowsAsync<Temno.Core.Account.HostedApiException>(() => client.TranscribeAsync(Audio, "en", null));
        Assert.Equal("transcription_account_changed", error.Code);
        account = "owner-a"; server.OnStatus = null;
        Assert.Equal("recognized", (await client.TranscribeAsync(Audio, "en", null)).Text);
        Assert.Equal(1, server.Admissions);
        Assert.Equal(0, server.Cancellations);
    }

    [Fact]
    public async Task Relaunch_after_finalization_does_not_upload_parts_again()
    {
        using var server = new TranscriptionServer { CrashPhase = "finalize" };
        using var http = new HttpClient(server);
        await Assert.ThrowsAsync<SimulatedProcessExit>(() => Client(http).TranscribeAsync(Audio, "en", null));
        Assert.Equal("recognized", (await Client(http).TranscribeAsync(Audio, "en", null)).Text);
        Assert.Equal(1, server.PartsWritten);
        Assert.Equal(1, server.Admissions);
    }

    [Fact]
    public async Task Separate_clients_serialize_the_same_persisted_request()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new TranscriptionServer { AdmissionWait = release.Task };
        using var http = new HttpClient(server);
        var first = Client(http).TranscribeAsync(Audio, "en", null);
        await server.AdmissionObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = Client(http).TranscribeAsync(Audio, "en", null);
        release.SetResult();
        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(results, result => Assert.Equal("recognized", result.Text));
        Assert.Equal(1, server.Admissions);
    }

    [Fact]
    public async Task Normal_app_exit_cancels_active_jobs_and_waits_for_acknowledgement()
    {
        using var server = new TranscriptionServer { KeepWorking = true };
        using var http = new HttpClient(server);
        var running = Client(http).TranscribeAsync(Audio, "en", null);
        await server.StatusObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await HostedTranscriptionClient.CancelActiveAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Equal(1, server.Cancellations);
        Assert.Empty(new HostedTranscriptionRequestStore(_root).Pending("owner-a", "https://transcription.example.test"));
    }

    [Fact]
    public async Task Saved_requests_are_partitioned_by_account_and_host()
    {
        using var server = new TranscriptionServer();
        using var http = new HttpClient(server);
        await Client(http).TranscribeAsync(Audio, "en", null);
        var other = new HostedTranscriptionClient("https://transcription.example.test", () => "token", http,
            new HostedTranscriptionRequestStore(_root), () => "owner-b");
        await other.TranscribeAsync(Audio, "en", null);
        var host = new HostedTranscriptionClient("https://other.example.test", () => "token", http,
            new HostedTranscriptionRequestStore(_root), () => "owner-a");
        await host.TranscribeAsync(Audio, "en", null);
        Assert.Equal(3, server.Admissions);
    }

    [Fact]
    public async Task One_corrupt_journal_does_not_block_a_valid_paid_request()
    {
        Directory.CreateDirectory(_root);
        var media = Path.Combine(_root, "recoverable.wav");
        File.WriteAllBytes(media, Audio.Bytes);
        using var server = new TranscriptionServer { CrashAfterAdmission = true };
        using var http = new HttpClient(server);
        await Assert.ThrowsAsync<SimulatedProcessExit>(() => Client(http).TranscribeAsync(Audio, "en", null, sourcePath: media));
        var folder = Path.GetDirectoryName(Directory.GetFiles(_root, "*.json", SearchOption.AllDirectories).Single())!;
        var corrupt = Path.Combine(folder, "broken.json");
        File.WriteAllText(corrupt, "{}");
        await Client(http).ResumePendingAsync(new NoAudioPreparation());
        Assert.True(TranscriptDiskCache.TryLoad(media, out var result));
        Assert.Equal("recognized", result!.Text);
        Assert.Equal(1, server.Admissions);
        Assert.Equal("{}", File.ReadAllText(corrupt));
    }

    [Fact]
    public async Task A_new_acknowledged_operation_recovers_to_its_current_source_path()
    {
        Directory.CreateDirectory(_root);
        var oldMedia = Path.Combine(_root, "old.wav");
        var newMedia = Path.Combine(_root, "new.wav");
        File.WriteAllBytes(oldMedia, Audio.Bytes); File.WriteAllBytes(newMedia, Audio.Bytes);
        using var server = new TranscriptionServer();
        using var http = new HttpClient(server);
        await Client(http).TranscribeAsync(Audio, "en", null, sourcePath: oldMedia,
            persistResult: result => TranscriptDiskCache.Store(oldMedia, result));
        File.Delete(TranscriptDiskCache.PathForKey(TranscriptDiskCache.KeyForPath(oldMedia)));
        server.CrashAfterAdmission = true;
        await Assert.ThrowsAsync<SimulatedProcessExit>(() => Client(http).TranscribeAsync(Audio, "en", null, sourcePath: newMedia));
        await Client(http).ResumePendingAsync(new NoAudioPreparation());
        Assert.True(TranscriptDiskCache.TryLoad(newMedia, out var result));
        Assert.Equal("recognized", result!.Text);
        Assert.Equal(2, server.Admissions);
    }

    private sealed class SimulatedProcessExit : Exception { }
    private sealed class NoAudioPreparation : IPcmWavPreparer
    {
        public Task<PreparedPcmWav> PrepareAsync(string path, CancellationToken ct = default)
            => throw new InvalidOperationException("Recovery must not prepare audio for an admitted job.");
    }

    private sealed class TranscriptionServer : HttpMessageHandler
    {
        public bool LoseAdmissionResponse;
        public bool CrashAfterAdmission, RejectCancellation, FailedOnce, KeepWorking;
        public string? FailPhase, CrashPhase;
        public Task? AdmissionWait;
        public TaskCompletionSource AdmissionObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource StatusObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Action? OnStatus;
        public CancellationTokenSource? CancelOnAdmission;
        public CancellationTokenSource? CancelOnStatus;
        public int Admissions, Cancellations, RequestCancellations, PartsWritten;
        public int Uploads => _uploads.Count;
        private readonly Dictionary<string, string> _uploads = [];
        private readonly Dictionary<string, string> _jobs = [];
        private readonly HashSet<string> _finalized = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/uploads") && request.Method == HttpMethod.Post)
            {
                var key = request.Headers.GetValues("Idempotency-Key").Single();
                if (!_uploads.TryGetValue(key, out var id)) _uploads[key] = id = Guid.NewGuid().ToString();
                Fail("upload");
                return Reply(new { id, partCount = 1 });
            }
            if (path.Contains("/parts/")) { PartsWritten++; Fail("part"); return Reply(new { stored = true }); }
            if (path.Contains("/uploads/") && request.Method == HttpMethod.Get)
                return Reply(new { id = path.Split('/').Last(), state = _finalized.Contains(path.Split('/').Last()) ? "finalized" : "uploading", quotedCredits = 5, storedParts = Array.Empty<int>() });
            if (path.EndsWith("/finalize")) { _finalized.Add(path.Split('/')[4]); Fail("finalize"); return Reply(new { id = path.Split('/')[4], quotedCredits = 5 }); }
            if (path.EndsWith("/jobs") && request.Method == HttpMethod.Post)
            {
                var key = request.Headers.GetValues("Idempotency-Key").Single();
                if (!_jobs.TryGetValue(key, out var id)) { _jobs[key] = id = Guid.NewGuid().ToString(); Admissions++; }
                if (CrashAfterAdmission) { CrashAfterAdmission = false; throw new SimulatedProcessExit(); }
                CancelOnAdmission?.Cancel();
                if (LoseAdmissionResponse) { LoseAdmissionResponse = false; throw new HttpRequestException("Response lost after admission"); }
                AdmissionObserved.TrySetResult();
                if (AdmissionWait is not null) return AdmissionReplyAsync(id);
                return Reply(new { id, status = "queued" });
            }
            if (path.EndsWith("/cancel") || path.EndsWith("/cancel-request"))
            {
                if (RejectCancellation) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") });
                if (path.Contains("/submissions/")) RequestCancellations++;
                Cancellations++; return Reply(new { status = "canceled" });
            }
            if (path.EndsWith("/result")) { Fail("result"); return Reply(new { text = "recognized", language = "en", words = Array.Empty<object>() }); }
            if (path.Contains("/jobs/"))
            {
                OnStatus?.Invoke();
                Fail("status");
                CancelOnStatus?.Cancel();
                StatusObserved.TrySetResult();
                return Reply(new { id = path.Split('/').Last(), status = CancelOnStatus is null && !KeepWorking ? "succeeded" : "working" });
            }
            throw new InvalidOperationException("Unexpected fixture request");
        }

        private void Fail(string phase)
        {
            if (CrashPhase == phase) { CrashPhase = null; throw new SimulatedProcessExit(); }
            if (FailPhase == phase && !FailedOnce) { FailedOnce = true; throw new HttpRequestException("Response lost"); }
        }

        private async Task<HttpResponseMessage> AdmissionReplyAsync(string id)
        {
            await AdmissionWait!;
            return await Reply(new { id, status = "queued" });
        }

        private static Task<HttpResponseMessage> Reply(object value) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
        });
    }
}
