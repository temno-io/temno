using System.Collections.Concurrent;
using System.Net.Http;
using Temno.Core.Account;
using Temno.Core.Generation;
using Temno.Core.IO;
using Temno.Core.Models;
using Temno.Core.Project;
using Temno.Core.Session;
using Temno.Environments;
using Temno.Generation;

namespace Temno.Mcp;

public static partial class ToolExecutor
{
    private static readonly ConcurrentDictionary<string, TaskCompletionSource> _hostedSettled = new();
    private static readonly ConcurrentDictionary<string, byte> _hostedDone = new();
    private static int _suspensionNoted;
    private static HostedPendingStore? _pendingStore;

    public static HostedPendingStore PendingGenerations
    {
        get => _pendingStore ??= new HostedPendingStore(Path.Combine(UserDataRoots.LocalAppDataRoot, "hosted-pending.json"));
        set => _pendingStore = value ?? throw new ArgumentNullException(nameof(value));
    }

    private static void ResetHostedGenerationsForTests()
    {
        HostedWatcherRegistry.Shared.StopAll();
        Interlocked.Exchange(ref _suspensionNoted, 0);
        _hostedSettled.Clear();
        _hostedDone.Clear();
        _pendingStore = new HostedPendingStore(Path.Combine(Path.GetTempPath(), "temno-hosted-pending", Guid.NewGuid().ToString("N"), "pending.json"));
    }

    public static void StartHostedWatcher()
    {
        var account = HostedRuntime.Session?.UserId;
        if (string.IsNullOrWhiteSpace(account))
            return;
        if (PendingGenerations.IsSuspended(account))
        {
            NoteSuspendedOnce();
            return;
        }

        PendingGenerations.AdoptAccount(account);
        EnsureHostedWatcher();
    }

    public static void WakeHostedWatcher()
    {
        EnsureHostedWatcher();
    }

    public static object ResumeHostedGeneration(string? mediaRef)
    {
        StartHostedWatcher();
        if (!string.IsNullOrWhiteSpace(mediaRef))
        {
            var session = GetSession();
            var entry = session.Manifest.Entries.FirstOrDefault(e => e.Id == mediaRef)
                ?? throw new InvalidOperationException($"Media asset not found: {mediaRef}");
            var package = session.PackagePath ?? throw new InvalidOperationException("Open project has no package path.");
            var resumed = IsImagesJob(entry)
                ? TryRecoverImagesJob(session, entry, package)
                : IsMediaJob(entry)
                    ? TryRecoverMediaJob(session, entry, package)
                    : RetryGeneration(mediaRef);
            if (!resumed)
                throw new InvalidOperationException("There is no pending generation to resume.");
            var described = GenerationEntryGuard.Describe(entry);
            return new Dictionary<string, object?>
            {
                ["status"] = "resumed",
                ["mediaRef"] = mediaRef,
                ["generationPhase"] = described?.Phase ?? HostedJobPhases.Pending,
                ["generationReason"] = described?.Reason,
                ["jobId"] = entry.GenerationInput?.BackendJobId,
            };
        }

        var account = HostedRuntime.Session?.UserId ?? "";
        var jobs = PendingGenerations.Active(HostedEndpoints.BuildEnvironment.Name(), account)
            .Select(job => new Dictionary<string, object?>
            {
                ["mediaRef"] = job.PlaceholderId,
                ["generationPhase"] = job.Phase,
                ["jobId"] = job.JobId,
                ["projectPath"] = job.ProjectPath,
            })
            .ToList();
        return new Dictionary<string, object> { ["status"] = "resumed", ["jobs"] = jobs };
    }

    public static IReadOnlyList<string> TakeHostedNotices()
        => PendingGenerations.TakeNotices().Select(notice => notice.Message).ToList();

    private static bool HostedTransportDeferred(ProjectSession session, string placeholderId, Exception exception)
    {
        if (!HostedStatusReads.IsStillWorking(exception) && !HostedStatusReads.IsSuspended(exception))
            return false;
        return ProjectHub.RunSerialized(() =>
            session.Manifest.Entries.FirstOrDefault(e => e.Id == placeholderId) is { } entry
            && (IsImagesJob(entry) || IsMediaJob(entry)));
    }

    private static Dictionary<string, object>? AlreadyOpenQuote(string quoteId)
    {
        var open = PendingGenerations.FindOpenByQuote(HostedRuntime.Session?.UserId ?? "", quoteId);
        if (open is null)
            return null;
        EnsureHostedWatcher();
        var result = GenerationAccepted(
            open.PlaceholderId,
            $"That quote is already {open.Phase}. Resume it instead of starting another. Placeholder asset ID: {open.PlaceholderId}.");
        result["generationPhase"] = open.Phase;
        if (!string.IsNullOrEmpty(open.JobId))
            result["jobId"] = open.JobId;
        return result;
    }

    private static async Task EnqueueAndWaitAsync(
        ProjectSession session, string placeholderId, string kind, string outputType, CancellationToken token)
    {
        RememberHostedJob(session, placeholderId, kind, outputType, submitInFlight: true);
        EnsureHostedWatcher();
        try
        {
            var job = PendingGenerations.FindByPlaceholder(placeholderId);
            if (job is null)
                return;
            var observation = await SubmitHostedJobAsync(job, token).ConfigureAwait(false);
            PendingGenerations.Update(placeholderId, current =>
            {
                current.SubmitInFlight = false;
                if (!string.IsNullOrEmpty(observation.JobId))
                    current.JobId = observation.JobId;
                if (observation.ResultUrls is { Count: > 0 })
                    current.ResultUrls = observation.ResultUrls.ToList();
                if (observation.CostCredits is long cost)
                    current.CostCredits = cost;
                if (observation.Kind == HostedWatchKind.Failed)
                {
                    current.Phase = HostedJobPhases.Failed;
                    current.ServerReason = observation.Reason;
                }
                else if (observation.Kind == HostedWatchKind.StillWorking && !string.IsNullOrEmpty(observation.Phase))
                {
                    current.Phase = observation.Phase;
                }
            });
            if (observation.Kind == HostedWatchKind.Suspended)
            {
                NoteSuspendedOnce();
                return;
            }
            if (observation.Kind == HostedWatchKind.Failed)
            {
                WriteHostedStatus(job, "failed: " + (observation.Reason ?? "The server refused the generation."), observation.JobId, null);
                CompleteHostedSettlement(placeholderId);
                return;
            }

            var watcher = EnsureHostedWatcher();
            if (observation.RetryAfter is { } retry)
                watcher?.HonorRetryAfter(retry);
            await WaitForHostedSettlement(placeholderId, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            PendingGenerations.Update(placeholderId, current => current.SubmitInFlight = false);
            await FinishHostedCancelAsync(session, placeholderId).ConfigureAwait(false);
        }
    }

    private static void RememberHostedJob(ProjectSession session, string placeholderId, string kind, string outputType, bool submitInFlight = false)
    {
        HostedPendingJob? job = null;
        ProjectHub.RunSerialized(() =>
        {
            var entry = session.Manifest.Entries.FirstOrDefault(e => e.Id == placeholderId);
            var input = entry?.GenerationInput;
            if (entry is null || input is null)
                return;
            if (string.IsNullOrEmpty(entry.GenerationStatus) || entry.GenerationStatus == "preparing")
                ApplyGenerationPlaceholderStatus(entry, "generating", input.BackendJobId, input.ResultUrls);
            GenerationEntryGuard.Touch(entry);
            session.MarkDirty();
            session.Save();
            job = new HostedPendingJob
            {
                Environment = HostedEndpoints.BuildEnvironment.Name(),
                AccountId = HostedRuntime.Session?.UserId ?? "",
                DeviceId = HostedDeviceId.GetOrCreate(Path.Combine(Path.GetDirectoryName(PendingGenerations.Path)!, "hosted-device-id")),
                ProjectPath = session.PackagePath ?? "",
                PlaceholderId = placeholderId,
                JobId = input.BackendJobId,
                QuoteId = input.QuoteId,
                SubmitKey = input.SubmitKey,
                Kind = kind,
                OutputType = outputType,
                Phase = PhaseFor(entry.GenerationStatus),
                Name = entry.Name,
                ResultUrls = input.ResultUrls,
                CancelRequested = input.CancelRequested == true,
                CantCancel = entry.GenerationStatus == HostedJobPhases.CantCancelStatus,
                SubmitInFlight = submitInFlight,
                CreatedAt = input.CreatedAt ?? DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
        });
        if (job is not null)
            PendingGenerations.Upsert(job);
    }

    private static string PhaseFor(string? status)
    {
        if (status is "rendering" or HostedJobPhases.CantCancelStatus)
            return HostedJobPhases.Rendering;
        if (status is "downloading" or "recovering")
            return HostedJobPhases.Recovering;
        return HostedJobPhases.Pending;
    }

    private static HostedJobWatcher? EnsureHostedWatcher()
    {
        var account = HostedRuntime.Session?.UserId;
        if (string.IsNullOrWhiteSpace(account))
            account = "unsigned";
        if (PendingGenerations.IsSuspended(account) && account != "unsigned")
            return null;
        var environment = HostedEndpoints.BuildEnvironment.Name();
        var watcher = HostedWatcherRegistry.Shared.Ensure(account, environment, () =>
        {
            var created = new HostedJobWatcher(PendingGenerations, new GatewayHostedTransport(), environment, account)
            {
                OnSettled = SettleHostedAsync,
                OnPhase = (job, phase) =>
                {
                    var fresh = PendingGenerations.FindByPlaceholder(job.PlaceholderId) ?? job;
                    var status = fresh.CantCancel
                        ? HostedJobPhases.CantCancelStatus
                        : HostedJobPhases.ManifestStatus(phase, cantCancel: false);
                    WriteHostedStatus(fresh, status, fresh.JobId, null);
                },
                OnSuspended = _ => NoteSuspendedOnce(),
            };
            return created;
        });
        watcher.Wake();
        return watcher;
    }

    private static async Task<bool> SettleHostedAsync(HostedPendingJob job, HostedJobObservation observation, CancellationToken cancellationToken)
    {
        if (observation.Kind == HostedWatchKind.Failed)
        {
            var reason = observation.Reason ?? "The server said this generation failed.";
            WriteHostedStatus(job, "failed: " + reason, observation.JobId ?? job.JobId, null);
            CompleteHostedSettlement(job.PlaceholderId);
            return true;
        }

        var urls = observation.ResultUrls ?? job.ResultUrls;
        if (urls is not { Count: > 0 })
            return false;

        var projectFile = Path.Combine(job.ProjectPath, ProjectConstants.TimelineFilename);
        var choice = HostedLandTarget.Choose(
            job.ProjectPath,
            File.Exists(projectFile),
            HostedLandTarget.RecoveredRoot(UserDataRoots.DocumentsRoot));
        try
        {
            var service = GetGenerationService();
            if (choice.Recovered)
            {
                Directory.CreateDirectory(choice.Directory);
                await service.DownloadAndLandAssetsAsync(urls, choice.Directory, job.PlaceholderId, cancellationToken)
                    .ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(choice.Notice))
                {
                    PendingGenerations.AddNotice("recovered", choice.Notice);
                    File.AppendAllText(Path.Combine(choice.Directory, "readme.txt"), choice.Notice + Environment.NewLine);
                }
            }
            else
            {
                var session = OpenHostedSession(job.ProjectPath);
                if (session is null)
                    return false;
                RememberCharge(session, job.PlaceholderId, observation.CostCredits);
                if (job.Kind == "image")
                {
                    await LandImageOutputsAsync(session, job.PlaceholderId, session.PackagePath!, urls, observation.JobId ?? job.JobId, cancellationToken, service)
                        .ConfigureAwait(false);
                }
                else
                {
                    await LandMediaOutputsAsync(
                            session, job.PlaceholderId, session.PackagePath!, urls, observation.JobId ?? job.JobId,
                            ParseOutputType(job.OutputType), cancellationToken, service)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (HostedStatusReads.IsStillWorking(ex) || ex is IOException or HttpRequestException or InvalidOperationException)
        {
            if (!string.IsNullOrEmpty(job.JobId))
                PendingGenerations.Update(job.PlaceholderId, current => current.ResultUrls = null);
            return false;
        }

        CompleteHostedSettlement(job.PlaceholderId);
        PendingGenerations.Update(job.PlaceholderId, current => current.Phase = HostedJobPhases.Landed);
        return true;
    }

    private static void RememberCharge(ProjectSession session, string placeholderId, long? cost)
    {
        if (cost is not long credits)
            return;
        ProjectHub.RunSerialized(() =>
        {
            var entry = session.Manifest.Entries.FirstOrDefault(e => e.Id == placeholderId);
            if (entry?.GenerationInput is null)
                return;
            entry.GenerationInput.ChargedCredits = (int)Math.Min(credits, int.MaxValue);
        });
    }

    private static ProjectSession? OpenHostedSession(string projectPath)
    {
        var active = ProjectHub.ActiveSession;
        if (active?.PackagePath is { } open && PathEquals(open, projectPath))
            return active;
        try
        {
            return ProjectSession.Open(projectPath);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void WriteHostedStatus(HostedPendingJob job, string status, string? jobId, IReadOnlyList<string>? urls)
    {
        var session = OpenHostedSession(job.ProjectPath);
        if (session is null)
            return;
        UpdatePlaceholderStatus(session, job.PlaceholderId, status, jobId, urls?.ToList());
    }

    private static async Task FinishHostedCancelAsync(ProjectSession session, string placeholderId)
    {
        var job = PendingGenerations.FindByPlaceholder(placeholderId);
        if (job is null)
        {
            var entry = ProjectHub.RunSerialized(() => session.Manifest.Entries.FirstOrDefault(e => e.Id == placeholderId));
            var kind = entry is not null && IsImagesJob(entry) ? "image" : "media";
            var output = entry?.Type.ToString() ?? "video";
            RememberHostedJob(session, placeholderId, kind, output);
            job = PendingGenerations.FindByPlaceholder(placeholderId);
        }
        if (job is null)
            return;
        PendingGenerations.Update(placeholderId, current => current.CancelRequested = true);
        job = PendingGenerations.FindByPlaceholder(placeholderId) ?? job;

        HostedCancelResult result;
        try
        {
            result = await CancelHostedAsync(job, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (HostedStatusReads.IsSuspended(ex))
        {
            NoteSuspendedOnce();
            return;
        }
        catch (Exception ex) when (HostedStatusReads.IsStillWorking(ex))
        {
            return;
        }

        await ApplyCancelResultAsync(job, result).ConfigureAwait(false);
    }

    private static async Task ApplyCancelResultAsync(HostedPendingJob job, HostedCancelResult result)
    {
        if (result.Outcome == HostedCancelResult.Suspended)
        {
            NoteSuspendedOnce();
            return;
        }
        if (result.Outcome == HostedCancelResult.AlreadyRendering)
        {
            PendingGenerations.Update(job.PlaceholderId, current =>
            {
                current.CancelRequested = false;
                current.CantCancel = true;
                current.Phase = HostedJobPhases.Rendering;
                if (!string.IsNullOrEmpty(result.Observation.JobId))
                    current.JobId = result.Observation.JobId;
            });
            WriteHostedStatus(job, HostedJobPhases.CantCancelStatus, result.Observation.JobId ?? job.JobId, null);
            return;
        }
        if (result.Outcome == HostedCancelResult.Succeeded)
        {
            PendingGenerations.Update(job.PlaceholderId, current =>
            {
                current.CancelRequested = false;
                current.ResultUrls = result.Observation.ResultUrls?.ToList() ?? current.ResultUrls;
                current.Phase = HostedJobPhases.Recovering;
                if (!string.IsNullOrEmpty(result.Observation.JobId))
                    current.JobId = result.Observation.JobId;
            });
            var fresh = PendingGenerations.FindByPlaceholder(job.PlaceholderId) ?? job;
            await SettleHostedAsync(fresh, result.Observation, CancellationToken.None).ConfigureAwait(false);
            return;
        }
        if (result.Outcome is HostedCancelResult.Canceled or HostedCancelResult.Failed)
        {
            var reason = result.Outcome == HostedCancelResult.Canceled
                ? "Cancellation requested"
                : result.Observation.Reason ?? "The server said this generation failed.";
            PendingGenerations.Update(job.PlaceholderId, current =>
            {
                current.Phase = HostedJobPhases.Failed;
                current.ServerReason = reason;
                current.CancelRequested = false;
            });
            WriteHostedStatus(job, "failed: " + reason, result.Observation.JobId ?? job.JobId, null);
            CompleteHostedSettlement(job.PlaceholderId);
            return;
        }
    }

    private static async Task<HostedCancelResult> CancelHostedAsync(HostedPendingJob job, CancellationToken cancellationToken)
    {
        if (job.Kind == "image")
        {
            var client = TryGetImageClient() ?? throw new InvalidOperationException(GenerationUnavailableMessage());
            if (!string.IsNullOrEmpty(job.JobId))
                return await client.CancelJobAsync(job.JobId, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(job.QuoteId) && Guid.TryParse(job.SubmitKey, out var imageKey))
                return await client.CancelSubmissionAsync(job.QuoteId, imageKey, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var client = TryGetMediaClient() ?? throw new InvalidOperationException(GenerationUnavailableMessage());
            if (!string.IsNullOrEmpty(job.JobId))
                return await client.CancelJobAsync(job.JobId, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(job.QuoteId) && Guid.TryParse(job.SubmitKey, out var mediaKey))
                return await client.CancelSubmissionAsync(job.QuoteId, mediaKey, cancellationToken).ConfigureAwait(false);
        }

        return HostedCancelResult.TransportStillWorking();
    }

    private static void NoteSuspendedOnce()
    {
        if (Interlocked.Exchange(ref _suspensionNoted, 1) == 1)
            return;
        var account = HostedRuntime.Session?.UserId ?? "";
        if (account.Length > 0)
            PendingGenerations.Suspend(account);
        PendingGenerations.AddNotice("suspended", HostedJobPhases.SuspendedMessage);
        if (account.Length == 0)
            return;
        foreach (var job in PendingGenerations.Active(HostedEndpoints.BuildEnvironment.Name(), account))
            WriteHostedStatus(job, HostedJobPhases.SuspendedMessage, job.JobId, null);
    }

    private static Task WaitForHostedSettlement(string placeholderId, CancellationToken token)
    {
        if (_hostedDone.ContainsKey(placeholderId))
            return Task.CompletedTask;
        var pending = _hostedSettled.GetOrAdd(
            placeholderId,
            _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        return _hostedDone.ContainsKey(placeholderId) ? Task.CompletedTask : pending.Task.WaitAsync(token);
    }

    private static void CompleteHostedSettlement(string placeholderId)
    {
        _hostedDone[placeholderId] = 1;
        if (_hostedSettled.TryGetValue(placeholderId, out var pending))
            pending.TrySetResult();
    }

    private static void AddHostedGenerationFields(Dictionary<string, object> asset, MediaManifestEntry entry)
    {
        var described = GenerationEntryGuard.Describe(entry);
        if (described is not { } phase)
            return;
        asset["generationPhase"] = phase.Phase;
        if (!string.IsNullOrEmpty(phase.Reason))
            asset["generationReason"] = phase.Reason;
        if (entry.GenerationInput?.BackendJobId is { Length: > 0 } jobId)
            asset["jobId"] = jobId;
    }

    private static ClipType ParseOutputType(string? outputType)
        => outputType switch
        {
            "audio" => ClipType.audio,
            "image" => ClipType.image,
            _ => ClipType.video,
        };

    private static async Task<HostedJobObservation> SubmitHostedJobAsync(HostedPendingJob job, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(job.QuoteId) || !Guid.TryParse(job.SubmitKey, out var key))
            return HostedJobObservation.Failed("The saved submission is missing.");
        if (job.Kind == "image")
        {
            var images = TryGetImageClient();
            return images is null
                ? HostedJobObservation.Working()
                : await images.SubmitObservedAsync(job.QuoteId, key, cancellationToken).ConfigureAwait(false);
        }

        var media = TryGetMediaClient();
        return media is null
            ? HostedJobObservation.Working()
            : await media.SubmitObservedAsync(job.QuoteId, key, cancellationToken).ConfigureAwait(false);
    }

    private sealed class GatewayHostedTransport : IHostedJobTransport
    {
        public async Task<HostedJobObservation> AdvanceAsync(HostedPendingJob job, CancellationToken cancellationToken)
        {
            try
            {
                if (job.CancelRequested)
                    return (await CancelHostedAsync(job, cancellationToken).ConfigureAwait(false)).Observation;
                if (!HostedJobPhases.NeedsFreshResultUrls(job.Phase, job.JobId) && job.ResultUrls is { Count: > 0 })
                    return HostedJobObservation.Succeeded(job.ResultUrls, job.JobId, job.CostCredits);
                if (string.IsNullOrEmpty(job.JobId))
                {
                    if (job.SubmitInFlight)
                        return HostedJobObservation.Working(job.Phase, job.JobId);
                    return await SubmitHostedJobAsync(job, cancellationToken).ConfigureAwait(false);
                }
                if (job.Kind == "image")
                {
                    var images = TryGetImageClient();
                    return images is null
                        ? HostedJobObservation.Working(job.Phase, job.JobId)
                        : await images.ReadStatusAsync(job.JobId, cancellationToken).ConfigureAwait(false);
                }

                var media = TryGetMediaClient();
                return media is null
                    ? HostedJobObservation.Working(job.Phase, job.JobId)
                    : await media.ReadStatusAsync(job.JobId, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (HostedStatusReads.IsSuspended(ex))
            {
                return HostedJobObservation.Suspended();
            }
            catch
            {
                return HostedJobObservation.Working(job.Phase, job.JobId);
            }
        }
    }
}
