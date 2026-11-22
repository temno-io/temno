using Temno.Core.Account;
using Temno.Core.IO;
using Temno.Core.Transcription;

namespace Temno.Media.Transcription;

/// <summary>Resolves the production transcription backend. Fake remains an explicit env opt-in.</summary>
public static class TranscriptionServices
{
    private static readonly object RecoveryGate = new();
    private static readonly Dictionary<string, Task> Recovery = [];
    private static readonly CancellationTokenSource Shutdown = new();

    private static HostedTranscriptionClient CloudClient()
    {
        var url = HostedEndpoints.ResolveGatewayUrl();
        if (string.IsNullOrWhiteSpace(url))
            throw new HostedApiException("Cloud transcription is not configured.", code: "transcription_not_ready");
        return new HostedTranscriptionClient(url, () => HostedRuntime.AccessToken);
    }

    public static void ResumeCloudRequests()
    {
        if (Shutdown.IsCancellationRequested || !HostedRuntime.IsSignedIn || !HostedEndpoints.GatewayConfigured()
            || HostedRuntime.Session?.UserId is not { Length: > 0 } account) return;
        lock (RecoveryGate)
        {
            if (Recovery.TryGetValue(account, out var prior) && !prior.IsCompleted) return;
            Recovery[account] = Task.Run(async () =>
            {
                try { await CloudClient().ResumePendingAsync(new PcmWavExtractor(), Shutdown.Token).ConfigureAwait(false); }
                catch (Exception) { System.Diagnostics.Debug.WriteLine("Cloud transcription recovery retained its pending requests."); }
            });
        }
    }

    public static async Task StopCloudRequestsAsync()
    {
        Shutdown.Cancel();
        await HostedTranscriptionClient.CancelActiveAsync().ConfigureAwait(false);
    }

    public static bool UseFakeProvider =>
        string.Equals(
            EnvCompat.Get("TEMNO_USE_FAKE_TRANSCRIPTION"),
            "true",
            StringComparison.OrdinalIgnoreCase);

    public static ITranscriptionProvider Resolve() => Resolve(cloud: false);

    public static ITranscriptionProvider Resolve(bool cloud, IProgress<string>? progress = null)
    {
        if (UseFakeProvider)
            return new FakeTranscriptionProvider();
        if (cloud)
        {
            return new CloudTranscriptionProvider(
                new PcmWavExtractor(),
                CloudClient,
                progress,
                Shutdown.Token);
        }

        return new WhisperNetTranscriptionProvider();
    }
}
