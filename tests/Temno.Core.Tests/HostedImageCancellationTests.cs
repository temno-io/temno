using System.Net;
using System.Text.Json.Nodes;
using Temno.Core.IO;
using Temno.Core.Session;
using Temno.Generation;
using Temno.Mcp;
using Xunit;

namespace Temno.Core.Tests;

[Collection("EnvRegistry")]
public sealed class HostedImageCancellationTests
{
    private sealed class Handler : HttpMessageHandler
    {
        public readonly string Quote = Guid.NewGuid().ToString();
        public readonly TaskCompletionSource Submitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Canceled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Creates;
        public string? SubmitKey;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            string json;
            switch (r.RequestUri!.AbsolutePath)
            {
                case "/v1/images/catalog":
                    json = """
                    {"version":"images-v1","configured":true,"acceptJobs":true,"qualificationOwner":true,
                     "models":[{"id":"muse-image","displayName":"Muse Image","family":"Muse",
                     "operations":[{"operation":"generate_image","available":true,"minInputs":0,"maxInputs":0,"maxOutputs":4}],"settings":[]}]}
                    """;
                    break;
                case "/v1/images/quotes":
                    json = System.Text.Json.JsonSerializer.Serialize(new { quoteId = Quote, model = "muse-image", operation = "generate_image",
                        credits = 4, expiresAt = DateTimeOffset.UtcNow.AddMinutes(2), outputCount = 1, settings = new { } });
                    break;
                case "/v1/images/jobs":
                    Interlocked.Increment(ref Creates);
                    SubmitKey = Assert.Single(r.Headers.GetValues("Idempotency-Key"));
                    Submitted.SetResult();
                    await Task.Delay(Timeout.Infinite, ct);
                    throw new InvalidOperationException("The simulated lost-response request should be canceled.");
                case "/v1/images/submissions/cancel":
                    Assert.Equal(SubmitKey, Assert.Single(r.Headers.GetValues("Idempotency-Key")));
                    Assert.Equal(Quote, JsonNode.Parse(await r.Content!.ReadAsStringAsync(ct))!["quoteId"]!.GetValue<string>());
                    Canceled.SetResult();
                    json = "{\"status\":\"canceled\"}";
                    break;
                default: throw new InvalidOperationException("Unexpected image request: " + r.RequestUri.AbsolutePath);
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        }
    }

    [Fact]
    public async Task Cancel_before_job_response_reconciles_original_submission_without_creating_another()
    {
        var folder = Path.Combine(Path.GetTempPath(), "temno-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var session = ProjectSession.CreateNew();
        session.Save(Path.Combine(folder, "fixture.temno"));
        ProjectHub.ActiveSession = session;
        UserPreferences.SetPathForTests(Path.Combine(folder, "prefs.json"));
        ToolExecutor.ResetGenerationService();
        var handler = new Handler();
        ToolExecutor.ImageClientOverride = new HostedImageClient("https://fixture.invalid", () => "test-token", new HttpClient(handler));
        ToolExecutor.GenerationServiceOverride = new GenerationService(new FakeGenerationClient());
        try
        {
            var quote = ToolExecutor.Execute("quote_image", new JsonObject { ["model"] = "muse-image", ["prompt"] = "Blue square" });
            Assert.False(quote.IsError, quote.Content[0].Text);
            var result = ToolExecutor.Execute("generate_image", new JsonObject { ["quoteId"] = handler.Quote, ["maxCredits"] = 4 });
            Assert.False(result.IsError, result.Content[0].Text);
            await handler.Submitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var entry = Assert.Single(session.Manifest.Entries);
            Assert.Null(entry.GenerationInput!.BackendJobId);
            Assert.True(ToolExecutor.CancelInFlightGeneration(entry.Id));
            await handler.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (int i = 0; i < 100 && entry.GenerationStatus != "failed: Cancellation requested"; i++) await Task.Delay(20);
            Assert.Equal("failed: Cancellation requested", entry.GenerationStatus);
            Assert.True(entry.GenerationInput.CancelRequested);
            Assert.Equal(1, handler.Creates);
            Assert.Equal(handler.SubmitKey, entry.GenerationInput.SubmitKey);
        }
        finally
        {
            ToolExecutor.GenerationServiceOverride = null;
            ToolExecutor.ResetGenerationService();
            ProjectHub.ActiveSession = null;
            UserPreferences.SetPathForTests(null);
            Directory.Delete(folder, true);
        }
    }
}
