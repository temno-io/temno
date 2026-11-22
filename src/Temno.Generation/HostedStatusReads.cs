using System.Text.Json;
using Temno.Core.Generation;

namespace Temno.Generation;

public sealed record HostedCancelResult(string Outcome, HostedJobObservation Observation)
{
    public const string Canceled = "canceled";
    public const string AlreadyRendering = "already_rendering";
    public const string Succeeded = "succeeded";
    public const string Transport = "transport";
    public const string Failed = "failed";
    public const string Suspended = "suspended";

    public static HostedCancelResult TransportStillWorking()
        => new(Transport, HostedJobObservation.Working());
}

/// <summary>
/// Turns a Gateway reply into a watch observation. A status read fails a job only when the job
/// itself is terminal. A submit may also fail when the server refuses it before a job exists.
/// </summary>
public static class HostedStatusReads
{
    public static bool IsSuspended(Exception exception)
        => exception is GenerationException gen
           && string.Equals(gen.Code, HostedJobPhases.AccountSuspendedCode, StringComparison.OrdinalIgnoreCase);

    public static bool IsStillWorking(Exception exception)
    {
        if (exception is OperationCanceledException)
            return false;
        if (exception is TimeoutException or HttpRequestException)
            return true;
        if (exception is GenerationException gen)
        {
            if (IsSuspended(gen))
                return false;
            if (gen.StatusCode is 401 or 408 or 429 or >= 500)
                return true;
            if (gen.Code is "rate_limited" or "authentication_required" or "unauthenticated")
                return true;
        }

        return exception.InnerException is not null && IsStillWorking(exception.InnerException);
    }

    public static HostedJobObservation FromStatusResponse(int statusCode, string? body, TimeSpan? retryAfter)
    {
        if (IsSuspendedBody(body))
            return HostedJobObservation.Suspended();
        if (statusCode is 401 or 404 or 408 or 429 or >= 500)
            return HostedJobObservation.Working(retryAfter: NonNegative(retryAfter));
        if (statusCode is < 200 or >= 300)
            return HostedJobObservation.Working(retryAfter: NonNegative(retryAfter));
        return FromJobBody(body) ?? HostedJobObservation.Working();
    }

    public static HostedJobObservation FromSubmitResponse(int statusCode, string? body, TimeSpan? retryAfter)
    {
        if (IsSuspendedBody(body))
            return HostedJobObservation.Suspended();
        if (IsAlreadyRendering(body))
            return KeepRendering(body);
        if (statusCode is >= 200 and < 300)
            return FromJobBody(body) ?? HostedJobObservation.Working();
        if (statusCode is 401 or 408 or 429 or >= 500)
            return HostedJobObservation.Working(retryAfter: NonNegative(retryAfter));

        var job = FromJobBody(body);
        if (job is { Kind: HostedWatchKind.Succeeded or HostedWatchKind.StillWorking })
            return job;
        if (statusCode is 400 or 402 or 403 or 404 or 409 or 410 or 422)
        {
            GenerationHttpErrors.TryParsePayload(body, out var code, out var message);
            var reason = string.IsNullOrWhiteSpace(message) ? code : message;
            return HostedJobObservation.Failed(string.IsNullOrWhiteSpace(reason) ? $"The server refused the generation (HTTP {statusCode})." : reason);
        }

        return HostedJobObservation.Working(retryAfter: NonNegative(retryAfter));
    }

    public static HostedCancelResult FromCancelResponse(int statusCode, string? body, TimeSpan? retryAfter)
    {
        if (IsSuspendedBody(body))
            return new HostedCancelResult(HostedCancelResult.Suspended, HostedJobObservation.Suspended());
        if (IsAlreadyRendering(body))
            return new HostedCancelResult(HostedCancelResult.AlreadyRendering, KeepRendering(body));
        if (statusCode is 401 or 404 or 408 or 429 or >= 500)
            return new HostedCancelResult(HostedCancelResult.Transport, HostedJobObservation.Working(retryAfter: NonNegative(retryAfter)));

        var job = FromJobBody(body);
        if (job is { Kind: HostedWatchKind.Succeeded })
            return new HostedCancelResult(HostedCancelResult.Succeeded, job);
        if (job is { Kind: HostedWatchKind.Failed })
        {
            var outcome = string.Equals(job.Reason, "canceled", StringComparison.OrdinalIgnoreCase)
                ? HostedCancelResult.Canceled
                : HostedCancelResult.Failed;
            var reason = outcome == HostedCancelResult.Canceled ? "Cancellation requested" : job.Reason;
            return new HostedCancelResult(outcome, HostedJobObservation.Failed(reason ?? "canceled", job.JobId));
        }
        if (job is { Phase: HostedJobPhases.Rendering })
            return new HostedCancelResult(HostedCancelResult.AlreadyRendering, job with { CantCancel = true, ClearCancel = true });
        if (statusCode is >= 200 and < 300 && BodyStatus(body) is "canceled" or "cancelled")
            return new HostedCancelResult(HostedCancelResult.Canceled, HostedJobObservation.Failed("Cancellation requested"));
        if (statusCode is >= 200 and < 300 && job is { Kind: HostedWatchKind.StillWorking })
            return new HostedCancelResult(HostedCancelResult.Transport, job);

        return new HostedCancelResult(HostedCancelResult.Transport, HostedJobObservation.Working(retryAfter: NonNegative(retryAfter)));
    }

    public static HostedJobObservation FromJob(string? id, string? status, string? error, IReadOnlyList<string>? urls, long? cost)
    {
        if (string.Equals(error, HostedJobPhases.AccountSuspendedCode, StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, HostedJobPhases.AccountSuspendedCode, StringComparison.OrdinalIgnoreCase))
            return HostedJobObservation.Suspended();
        if (urls is { Count: > 0 } || string.Equals(status, "succeeded", StringComparison.OrdinalIgnoreCase))
            return HostedJobObservation.Succeeded(urls, id, cost);
        if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
            return HostedJobObservation.Failed(string.IsNullOrWhiteSpace(error) ? "failed" : error, id);
        if (string.Equals(status, "canceled", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase))
            return HostedJobObservation.Failed(string.IsNullOrWhiteSpace(error) ? "canceled" : error, id);
        if (status is "running" or "rendering" or "processing")
            return HostedJobObservation.Working(HostedJobPhases.Rendering, id);
        return HostedJobObservation.Working(HostedJobPhases.Pending, id);
    }

    private static HostedJobObservation KeepRendering(string? body)
    {
        var job = FromJobBody(body);
        var id = job?.JobId;
        return HostedJobObservation.Working(HostedJobPhases.Rendering, id) with { CantCancel = true, ClearCancel = true };
    }

    private static HostedJobObservation? FromJobBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("status", out _))
                return null;
            var id = ReadString(root, "id");
            var status = ReadString(root, "status");
            var error = ReadString(root, "error");
            long? cost = null;
            if (root.TryGetProperty("costCredits", out var costEl) && costEl.TryGetInt64(out var credits))
                cost = credits;
            List<string>? urls = null;
            if (root.TryGetProperty("resultUrls", out var urlEl) && urlEl.ValueKind == JsonValueKind.Array)
            {
                urls = [];
                foreach (var item in urlEl.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } url)
                        urls.Add(url);
                }
            }

            return FromJob(id, status, error, urls, cost);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsSuspendedBody(string? body)
    {
        GenerationHttpErrors.TryParsePayload(body, out var code, out _);
        return string.Equals(code, HostedJobPhases.AccountSuspendedCode, StringComparison.OrdinalIgnoreCase)
            || string.Equals(BodyStatus(body), HostedJobPhases.AccountSuspendedCode, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAlreadyRendering(string? body)
    {
        GenerationHttpErrors.TryParsePayload(body, out var code, out _);
        return string.Equals(code, HostedJobPhases.AlreadyRenderingCode, StringComparison.OrdinalIgnoreCase);
    }

    private static string? BodyStatus(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                ? ReadString(doc.RootElement, "status")
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el) || el.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        return el.ValueKind == JsonValueKind.String ? el.GetString() : el.ToString();
    }

    private static TimeSpan? NonNegative(TimeSpan? retryAfter)
        => retryAfter is { } wait && wait < TimeSpan.Zero ? TimeSpan.Zero : retryAfter;
}
