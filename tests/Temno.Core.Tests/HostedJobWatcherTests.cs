using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Temno.Core.Generation;
using Temno.Core.Models;
using Temno.Core.Session;
using Temno.Generation;
using Xunit;

namespace Temno.Core.Tests;

public class HostedJobWatcherTests
{
    [Fact]
    public void A_failed_download_asks_for_fresh_result_urls()
    {
        Assert.True(HostedJobPhases.NeedsFreshResultUrls(HostedJobPhases.Recovering, "job-1"));
        Assert.False(HostedJobPhases.NeedsFreshResultUrls(HostedJobPhases.Pending, "job-1"));
        Assert.False(HostedJobPhases.NeedsFreshResultUrls(HostedJobPhases.Recovering, null));
    }

    [Fact]
    public async Task Wake_does_not_skip_retry_after()
    {
        var root = Path.Combine(Path.GetTempPath(), "wp343-" + Guid.NewGuid().ToString("N"));
        var store = new HostedPendingStore(Path.Combine(root, "pending.json"));
        store.Upsert(OpenJob());
        var delays = new List<TimeSpan>();
        var transport = new WakeTransport();
        var watcher = new HostedJobWatcher(
            store,
            transport,
            "development",
            "account-1",
            delay: (gap, _) =>
            {
                delays.Add(gap);
                return Task.CompletedTask;
            },
            nextGap: _ => TimeSpan.FromSeconds(2));
        transport.OnRead = () => watcher.Wake();
        Assert.True(await watcher.StepAsync(CancellationToken.None));
        Assert.InRange(delays[0].TotalSeconds, 29, 31);
    }

    private sealed class WakeTransport : IHostedJobTransport
    {
        public Action? OnRead { get; set; }

        public Task<HostedJobObservation> AdvanceAsync(HostedPendingJob job, CancellationToken cancellationToken)
        {
            OnRead?.Invoke();
            return Task.FromResult(HostedJobObservation.Working(retryAfter: TimeSpan.FromSeconds(30)));
        }
    }

    [Fact]
    public async Task Transport_errors_never_fail_and_the_job_lands()
    {
        var root = Path.Combine(Path.GetTempPath(), "wp343-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = new HostedPendingStore(Path.Combine(root, "pending.json"));
        var job = OpenJob();
        store.Upsert(job);
        var waits = new List<TimeSpan>();
        var transport = new ScriptTransport(
            HostedJobObservation.Working(retryAfter: TimeSpan.FromSeconds(30)),
            HostedStatusReads.FromStatusResponse(502, "", null),
            HostedJobObservation.Working(),
            HostedJobObservation.Working(),
            HostedStatusReads.FromStatusResponse(401, """{"error":{"code":"authentication_required"}}""", null),
            HostedJobObservation.Succeeded(["https://cdn.example/out.mp4"], job.JobId, 12));
        var landed = 0;
        string? failed = null;
        var now = DateTimeOffset.Parse("2026-10-06T12:00:00Z");
        var watcher = new HostedJobWatcher(
            store,
            transport,
            "development",
            "account-1",
            delay: (gap, _) =>
            {
                waits.Add(gap);
                now = now.Add(gap);
                return Task.CompletedTask;
            },
            nextGap: retry => HostedJobWatcher.NextGap(new Random(1), retry),
            clock: () => now);
        watcher.OnSettled = (_, observation, _) =>
        {
            if (observation.Kind == HostedWatchKind.Failed)
                failed = observation.Reason;
            else
                landed++;
            return Task.FromResult(true);
        };

        Assert.True(await watcher.StepAsync(CancellationToken.None));
        Assert.Equal(TimeSpan.FromSeconds(30), waits[0]);
        Assert.Equal(HostedJobPhases.Pending, store.FindByPlaceholder(job.PlaceholderId)!.Phase);

        for (var i = 0; i < 4; i++)
            Assert.True(await watcher.StepAsync(CancellationToken.None));
        Assert.Null(failed);
        Assert.NotEqual(HostedJobPhases.Failed, store.FindByPlaceholder(job.PlaceholderId)!.Phase);

        Assert.True(await watcher.StepAsync(CancellationToken.None));
        Assert.Equal(1, landed);
        Assert.Null(failed);
        Assert.Equal(HostedJobPhases.Landed, store.FindByPlaceholder(job.PlaceholderId)!.Phase);
    }

    [Fact]
    public async Task Scripted_gateway_answers_429_502_timeout_drop_and_expired_token()
    {
        var calls = 0;
        var handler = new StubHandler(request =>
        {
            calls++;
            return calls switch
            {
                1 => Response(HttpStatusCode.TooManyRequests, "", TimeSpan.FromSeconds(30)),
                2 => Response(HttpStatusCode.BadGateway, ""),
                3 => throw new HttpRequestException("dropped"),
                4 => throw new TaskCanceledException("timed out"),
                5 => Response(HttpStatusCode.Unauthorized, """{"error":{"code":"authentication_required"}}"""),
                _ => Response(HttpStatusCode.OK, """{"id":"job-1","status":"succeeded","resultUrls":["https://cdn.example/out.mp4"],"costCredits":4}"""),
            };
        });
        var client = new HostedMediaClient("https://gateway.test", () => "token", new HttpClient(handler));
        var root = Path.Combine(Path.GetTempPath(), "wp343-" + Guid.NewGuid().ToString("N"));
        var store = new HostedPendingStore(Path.Combine(root, "pending.json"));
        var job = OpenJob();
        store.Upsert(job);
        var phases = new List<string>();
        var gaps = new List<TimeSpan>();
        var now = DateTimeOffset.Parse("2026-10-06T12:00:00Z");
        var watcher = new HostedJobWatcher(
            store,
            new ClientTransport(client),
            "development",
            "account-1",
            delay: (gap, _) =>
            {
                now = now.Add(gap);
                return Task.CompletedTask;
            },
            nextGap: retry =>
            {
                gaps.Add(retry ?? TimeSpan.FromSeconds(2));
                return retry ?? TimeSpan.FromSeconds(2);
            },
            clock: () => now);
        watcher.OnSettled = (_, _, _) => Task.FromResult(true);
        while (store.FindByPlaceholder(job.PlaceholderId)!.Phase != HostedJobPhases.Landed)
        {
            phases.Add(store.FindByPlaceholder(job.PlaceholderId)!.Phase);
            Assert.True(await watcher.StepAsync(CancellationToken.None));
            Assert.NotEqual(HostedJobPhases.Failed, store.FindByPlaceholder(job.PlaceholderId)!.Phase);
            if (phases.Count > 8)
                break;
        }

        Assert.Equal(HostedJobPhases.Landed, store.FindByPlaceholder(job.PlaceholderId)!.Phase);
        Assert.Contains(TimeSpan.FromSeconds(30), gaps);
        Assert.True(calls >= 6);
    }

    [Fact]
    public void Retry_after_is_honoured_and_the_gap_stays_between_two_and_ten_seconds()
    {
        var random = new Random(4);
        var forced = HostedJobWatcher.NextGap(random, TimeSpan.FromSeconds(45));
        Assert.Equal(TimeSpan.FromSeconds(45), forced);
        for (var i = 0; i < 40; i++)
        {
            var gap = HostedJobWatcher.NextGap(random, TimeSpan.FromSeconds(1));
            Assert.InRange(gap.TotalSeconds, 2, 10);
        }
    }

    [Fact]
    public async Task Account_suspended_is_noted_once_and_not_retried()
    {
        var root = Path.Combine(Path.GetTempPath(), "wp343-" + Guid.NewGuid().ToString("N"));
        var store = new HostedPendingStore(Path.Combine(root, "pending.json"));
        var job = OpenJob();
        store.Upsert(job);
        var reads = 0;
        var notes = 0;
        var transport = new ScriptTransport(HostedJobObservation.Suspended(), HostedJobObservation.Succeeded(["https://cdn.example/x"], job.JobId, 1));
        var watcher = new HostedJobWatcher(store, new CountingTransport(transport, () => reads++), "development", "account-1",
            delay: (_, _) => Task.CompletedTask, nextGap: _ => TimeSpan.FromSeconds(2))
        {
            OnSuspended = _ => notes++,
        };

        Assert.False(await watcher.StepAsync(CancellationToken.None));
        Assert.False(await watcher.StepAsync(CancellationToken.None));
        Assert.Equal(1, reads);
        Assert.Equal(1, notes);
        Assert.True(store.IsSuspended("account-1"));
        Assert.NotEqual(HostedJobPhases.Failed, store.FindByPlaceholder(job.PlaceholderId)!.Phase);
    }

    [Fact]
    public void A_second_ensure_does_not_start_another_watcher()
    {
        var registry = new HostedWatcherRegistry();
        var root = Path.Combine(Path.GetTempPath(), "wp343-" + Guid.NewGuid().ToString("N"));
        var store = new HostedPendingStore(Path.Combine(root, "pending.json"));
        HostedJobWatcher Make() => new(store, new ScriptTransport(), "development", "account-1",
            delay: (_, token) => Task.Delay(Timeout.Infinite, token), nextGap: _ => TimeSpan.FromHours(1));
        var first = registry.Ensure("account-1", "development", Make);
        var second = registry.Ensure("account-1", "development", Make);
        Assert.Same(first, second);
        Assert.Equal(1, registry.Count);
        var other = registry.Ensure("account-1", "production", Make);
        Assert.NotSame(first, other);
        Assert.Equal(2, registry.Count);
        registry.StopAll();
    }

    [Fact]
    public void Pending_list_round_trips_outside_any_project()
    {
        var root = Path.Combine(Path.GetTempPath(), "wp343-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "pending.json");
        var store = new HostedPendingStore(path);
        store.Upsert(OpenJob());
        var again = new HostedPendingStore(path);
        var loaded = again.FindByPlaceholder("PLACE");
        Assert.NotNull(loaded);
        Assert.Equal("development", loaded!.Environment);
        Assert.Equal("account-1", loaded.AccountId);
        Assert.Equal("device-1", loaded.DeviceId);
        Assert.Equal(@"D:\cuts\one", loaded.ProjectPath);
        Assert.Equal("job-1", loaded.JobId);
        Assert.Equal("quote-1", loaded.QuoteId);
        Assert.Equal("submit-key", loaded.SubmitKey);
    }

    [Fact]
    public void Deleted_project_keeps_the_file_and_says_so()
    {
        var open = HostedLandTarget.Choose(@"D:\cuts\one", projectExists: true, @"D:\Temno\recovered-results");
        Assert.False(open.Recovered);
        Assert.EndsWith(Path.Combine("cuts", "one"), open.Directory);

        var gone = HostedLandTarget.Choose(@"D:\cuts\missing", projectExists: false, @"D:\Temno\recovered-results");
        Assert.True(gone.Recovered);
        Assert.EndsWith("recovered-results", gone.Directory);
        Assert.Contains(@"D:\cuts\missing", gone.Notice);
        Assert.Contains("recovered-results", gone.Notice);
    }

    [Fact]
    public void Undo_and_redo_keep_a_running_and_a_landed_generation()
    {
        var session = ProjectSession.CreateNew();
        session.RecordBeforeEdit();
        var running = Entry("RUN", "generating");
        session.Manifest.Entries.Add(running);
        session.Undo();
        Assert.Contains(session.Manifest.Entries, entry => entry.Id == "RUN" && entry.GenerationStatus == "generating");

        session.RecordBeforeEdit();
        var current = session.Manifest.Entries.Single(entry => entry.Id == "RUN");
        current.GenerationStatus = null;
        current.GenerationInput!.BackendJobId = "job-1";
        current.Source = MediaSource.Project("media/gen-landed.mp4");
        GenerationEntryGuard.Touch(current);
        session.Undo();
        var kept = session.Manifest.Entries.Single(entry => entry.Id == "RUN");
        Assert.Null(kept.GenerationStatus);
        Assert.Equal("job-1", kept.GenerationInput!.BackendJobId);
        Assert.Contains("gen-landed.mp4", kept.Source.ToString());

        session.Redo();
        kept = session.Manifest.Entries.Single(entry => entry.Id == "RUN");
        Assert.Null(kept.GenerationStatus);
        Assert.Equal("job-1", kept.GenerationInput!.BackendJobId);
    }

    [Fact]
    public void Undo_still_removes_an_import_placeholder()
    {
        var session = ProjectSession.CreateNew();
        session.RecordBeforeEdit();
        session.Manifest.Entries.Add(new MediaManifestEntry
        {
            Id = "IMP",
            Name = "clip",
            GenerationStatus = "downloading",
            ImportInput = new MediaImportInput { SourcePath = @"D:\clip.mp4" },
        });
        session.Undo();
        Assert.DoesNotContain(session.Manifest.Entries, entry => entry.Id == "IMP");
    }

    [Fact]
    public void Cancel_already_rendering_keeps_the_job_and_success_lands_it()
    {
        var rendering = HostedStatusReads.FromCancelResponse(409, """{"error":{"code":"already_rendering"}}""", null);
        Assert.Equal(HostedCancelResult.AlreadyRendering, rendering.Outcome);
        Assert.Equal(HostedWatchKind.StillWorking, rendering.Observation.Kind);
        Assert.True(rendering.Observation.CantCancel);

        var landed = HostedStatusReads.FromCancelResponse(200,
            """{"id":"job-1","status":"succeeded","resultUrls":["https://cdn.example/out.mp4"]}""", null);
        Assert.Equal(HostedCancelResult.Succeeded, landed.Outcome);
        Assert.Equal("https://cdn.example/out.mp4", Assert.Single(landed.Observation.ResultUrls!));

        var dropped = HostedStatusReads.FromCancelResponse(502, "", null);
        Assert.Equal(HostedCancelResult.Transport, dropped.Outcome);
        Assert.Equal(HostedWatchKind.StillWorking, dropped.Observation.Kind);
    }

    [Fact]
    public void Phases_match_the_manifest()
    {
        Assert.Equal((HostedJobPhases.Pending, (string?)null), GenerationEntryGuard.Describe(Entry("A", "generating")));
        var rendering = Entry("B", HostedJobPhases.CantCancelStatus);
        Assert.Equal(HostedJobPhases.Rendering, GenerationEntryGuard.Describe(rendering)!.Value.Phase);
        var failed = Entry("C", "failed: quota");
        Assert.Equal((HostedJobPhases.Failed, "quota"), GenerationEntryGuard.Describe(failed));
        var landed = Entry("D", null);
        landed.GenerationInput!.BackendJobId = "job-1";
        Assert.Equal(HostedJobPhases.Landed, GenerationEntryGuard.Describe(landed)!.Value.Phase);
    }

    [Fact]
    public void Checkout_watch_covers_about_ten_minutes_with_growing_gaps()
    {
        var gaps = CheckoutWatchSchedule.Gaps();
        Assert.Equal(TimeSpan.FromMinutes(10), new TimeSpan(gaps.Sum(gap => gap.Ticks)));
        Assert.Equal(TimeSpan.FromSeconds(4), gaps[0]);
        Assert.True(gaps[1] > gaps[0]);
        Assert.Equal(TimeSpan.FromSeconds(60), gaps[^1]);
    }

    [Fact]
    public void Temno_and_Temno_Dev_do_not_share_an_instance_name()
    {
        Assert.NotEqual(SingleInstanceNames.Mutex("development"), SingleInstanceNames.Mutex("production"));
        Assert.NotEqual(SingleInstanceNames.Activate("development"), SingleInstanceNames.Activate("production"));
    }

    private static HostedPendingJob OpenJob() => new()
    {
        Environment = "development",
        AccountId = "account-1",
        DeviceId = "device-1",
        ProjectPath = @"D:\cuts\one",
        PlaceholderId = "PLACE",
        JobId = "job-1",
        QuoteId = "quote-1",
        SubmitKey = "submit-key",
        Kind = "media",
        OutputType = "video",
        Phase = HostedJobPhases.Pending,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static MediaManifestEntry Entry(string id, string? status) => new()
    {
        Id = id,
        Name = id,
        GenerationStatus = status,
        GenerationInput = new GenerationInput { Prompt = "a", Model = "m", QuoteId = "q", SubmitKey = Guid.NewGuid().ToString("D") },
    };

    private static HttpResponseMessage Response(HttpStatusCode status, string body, TimeSpan? retryAfter = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (retryAfter is { } wait)
            response.Headers.RetryAfter = new RetryConditionHeaderValue(wait);
        return response;
    }

    private sealed class ScriptTransport(params HostedJobObservation[] observations) : IHostedJobTransport
    {
        private int _index;
        public Task<HostedJobObservation> AdvanceAsync(HostedPendingJob job, CancellationToken cancellationToken)
        {
            if (observations.Length == 0)
                return Task.FromResult(HostedJobObservation.Working(job.Phase, job.JobId));
            var observation = observations[Math.Min(_index, observations.Length - 1)];
            if (_index < observations.Length)
                _index++;
            return Task.FromResult(observation);
        }
    }

    private sealed class CountingTransport(IHostedJobTransport inner, Action count) : IHostedJobTransport
    {
        public Task<HostedJobObservation> AdvanceAsync(HostedPendingJob job, CancellationToken cancellationToken)
        {
            count();
            return inner.AdvanceAsync(job, cancellationToken);
        }
    }

    private sealed class ClientTransport(HostedMediaClient client) : IHostedJobTransport
    {
        public Task<HostedJobObservation> AdvanceAsync(HostedPendingJob job, CancellationToken cancellationToken)
            => client.ReadStatusAsync(job.JobId!, cancellationToken);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(send(request));
    }
}
