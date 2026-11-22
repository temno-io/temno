using System.Text.Json;
using Temno.Core.Models;

namespace Temno.Core.Generation;

/// <summary>
/// Undo and redo swap the whole manifest. A running or landed generation is put back afterwards,
/// including when the snapshot would return a finished one to pending.
/// </summary>
public static class GenerationEntryGuard
{
    public static void Touch(MediaManifestEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        entry.GenerationStamp = entry.GenerationStamp == long.MaxValue
            ? long.MaxValue
            : entry.GenerationStamp + 1;
    }

    public static void Retain(MediaManifest live, MediaManifest restored)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(restored);
        restored.Entries ??= [];
        foreach (var entry in live.Entries)
        {
            if (!IsProtected(entry))
                continue;
            var previous = restored.Entries.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, entry.Id, StringComparison.Ordinal));
            if (previous is null)
            {
                restored.Entries.Add(Clone(entry));
                continue;
            }

            if (Rank(entry) > Rank(previous) || entry.GenerationStamp > previous.GenerationStamp)
                CopyOutcome(entry, previous);
        }
    }

    public static bool IsProtected(MediaManifestEntry entry)
    {
        if (entry.GenerationInput is null)
            return false;
        if (entry.ImportInput is not null
            && string.IsNullOrEmpty(entry.GenerationInput.QuoteId)
            && string.IsNullOrEmpty(entry.GenerationInput.BackendJobId)
            && string.IsNullOrEmpty(entry.GenerationInput.Operation))
            return false;
        return Rank(entry) > 0;
    }

    public static int Rank(MediaManifestEntry entry)
    {
        var status = entry.GenerationStatus ?? "";
        if (status.StartsWith("failed:", StringComparison.Ordinal))
            return 0;
        if (status == "preparing")
            return 10;
        if (status is "generating" or "pending")
            return 20;
        if (status is "rendering" or HostedJobPhases.CantCancelStatus)
            return 30;
        if (status is "downloading" or "recovering")
            return 40;
        if (string.IsNullOrEmpty(status) || status is "none" or "demo")
        {
            var input = entry.GenerationInput;
            if (input is not null && (entry.GenerationStamp > 0
                || !string.IsNullOrEmpty(input.BackendJobId)
                || input.ResultUrls is { Count: > 0 }
                || !string.IsNullOrEmpty(input.QuoteId)))
                return 50;
        }

        return 0;
    }

    public static (string Phase, string? Reason)? Describe(MediaManifestEntry entry)
    {
        if (entry.GenerationInput is null)
            return null;
        if (entry.ImportInput is not null
            && string.IsNullOrEmpty(entry.GenerationInput.QuoteId)
            && string.IsNullOrEmpty(entry.GenerationInput.BackendJobId)
            && string.IsNullOrEmpty(entry.GenerationInput.Operation))
            return null;

        var status = entry.GenerationStatus ?? "";
        if (status.StartsWith("failed:", StringComparison.Ordinal))
            return (HostedJobPhases.Failed, status["failed:".Length..].Trim());
        if (string.Equals(status, HostedJobPhases.SuspendedMessage, StringComparison.Ordinal))
            return (HostedJobPhases.Failed, HostedJobPhases.AccountSuspendedCode);
        if (status is "rendering" or HostedJobPhases.CantCancelStatus)
            return (HostedJobPhases.Rendering, null);
        if (status is "downloading" or "recovering")
            return (HostedJobPhases.Recovering, null);
        if (status is "preparing" or "generating" or "pending")
            return (HostedJobPhases.Pending, null);
        if (Rank(entry) >= 50)
            return (HostedJobPhases.Landed, null);
        return null;
    }

    private static void CopyOutcome(MediaManifestEntry from, MediaManifestEntry to)
    {
        var copy = Clone(from);
        to.GenerationStatus = copy.GenerationStatus;
        to.GenerationStamp = copy.GenerationStamp;
        to.GenerationInput = copy.GenerationInput;
        to.Source = copy.Source;
        to.Type = copy.Type;
        to.Duration = copy.Duration;
        to.SourceWidth = copy.SourceWidth;
        to.SourceHeight = copy.SourceHeight;
        to.SourceFps = copy.SourceFps;
        to.HasAudio = copy.HasAudio;
    }

    private static MediaManifestEntry Clone(MediaManifestEntry entry)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(entry, JsonOptions.ForProject);
        return JsonSerializer.Deserialize<MediaManifestEntry>(json, JsonOptions.ForProject)
            ?? throw new InvalidOperationException("Could not copy a generation entry.");
    }
}
