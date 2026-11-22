using System.Net.Http;
using System.Text.Json;
using Temno.Core.Agent;

namespace Temno.Core.Account;

public static class HostedApiErrors
{
    public static async Task ThrowIfUnsuccessfulAsync(HttpResponseMessage response, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.IsSuccessStatusCode)
            return;

        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            body = "";
        }

        throw FromHttp((int)response.StatusCode, body);
    }

    public static HostedApiException FromHttp(int status, string? body)
    {
        TryParse(body, out var code, out var message);
        var described = Describe(status, code, message);
        long? suggested = null;
        // WP-254: a quote rejection carries the figures that explain it; state the adjustment, never make it.
        if (ReadDetails(body) is { } details)
        {
            if (code == "spending_cap_too_small" && details.TryGetProperty("minimumCapCredits", out var min) && min.TryGetInt64(out var minimum))
            {
                described = $"This model and effort need a spending cap of at least {minimum} credits for one useful step plus a closing report.";
                if (details.TryGetProperty("requiredCapCredits", out var full) && full.TryGetInt64(out var required) && required > minimum)
                    described += $" {required} credits allow the full output size on the first step.";
                if (details.TryGetProperty("typicalCallCredits", out var typical) && typical.TryGetInt64(out var usual))
                    described += $" A typical step costs about {usual} credits; unused credits return at settlement.";
                // Only a submission saved with an explicit cap reaches this; Send estimates the hold instead.
                described += " Send it again to let Temno estimate the hold, or choose a lower effort. Nothing was charged.";
            }
            else if (code == "context_too_large")
                described = "This conversation is too long for the selected model to take another step and still report. Start a new chat or remove earlier context. Nothing was charged.";
            else if (code == "insufficient_credits" && Long(details, "minimumHoldCredits") is { } needed && Long(details, "availableCredits") is { } available)
                described = $"This model and effort need {Credits(needed)} credits available to start. You have {Credits(available)}. "
                    + "Open Account and choose a plan, or choose another model. Nothing was charged.";
            else if (code == "hold_confirmation_required" && Long(details, "suggestedHoldCredits") is { } hold)
            {
                suggested = hold;
                var likely = Long(details, "likelyLowCredits") is { } low && Long(details, "likelyHighCredits") is { } high
                    ? $" (likely {HostedEstimate.Range(low, high)})" : "";
                described = $"This long conversation needs up to {Credits(hold)} credits held{likely}. You pay only what it uses; "
                    + "unused credits return when it finishes. Nothing is held unless you continue.";
            }
        }
        return new HostedApiException(described, status, code) { SuggestedHoldCredits = suggested };
    }

    private static long? Long(JsonElement details, string name) =>
        details.TryGetProperty(name, out var value) && value.TryGetInt64(out var n) ? n : null;
    public static string Credits(long credits) => credits.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

    private static JsonElement? ReadDetails(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Object
                ? details.Clone() : null;
        }
        catch (JsonException) { return null; }
    }

    public static string Describe(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        if (ex is HostedApiException hosted)
            return hosted.Message;
        if (ex is HttpRequestException http)
            return string.IsNullOrWhiteSpace(http.Message) ? "Hosted request failed." : http.Message;
        if (ex is TimeoutException)
            return "The hosted request timed out.";
        if (ex is OperationCanceledException)
            return "Cancelled";
        return string.IsNullOrWhiteSpace(ex.Message) ? "Hosted request failed." : ex.Message;
    }

    public static string Describe(int? status, string? code, string? message)
    {
        var trimmed = string.IsNullOrWhiteSpace(message) ? "" : message.Trim();
        if (trimmed.Length > 500)
            trimmed = trimmed[..500];
        if (!string.IsNullOrEmpty(trimmed)
            && !string.Equals(trimmed, code, StringComparison.OrdinalIgnoreCase))
            return trimmed;

        var normalized = string.IsNullOrWhiteSpace(code) ? "" : code.Trim().ToLowerInvariant();
        return normalized switch
        {
            "insufficient_credits" => "Insufficient credits. Open Account and choose a plan.",
            "credit_account_required" => "No hosted credit account yet. Hosted generation is optional.",
            "rate_limited" => "Rate limited. Try again later.",
            "authentication_required" or "unauthenticated" => "Sign in to use hosted credits.",
            "not_configured" => "Hosted services are not configured on this build.",
            "generation_disabled" => "Hosted generation is turned off on the gateway.",
            "agent_not_ready" => "Hosted chat is not configured.",
            "transcription_not_ready" => "Cloud transcription is not configured.",
            "dependency_unavailable" or "artifact_store_unavailable" => "The hosted provider is down.",
            "openrouter_unfunded" or "transcription_provider_unfunded" or "transcription_funding_unavailable"
                => "The hosted provider is down.",
            "idempotency_key_required" => "The hosted request was missing its idempotency key.",
            "invalid_request" or "invalid_audio" or "invalid_audio_upload" or "invalid_transcription_request"
                => "The hosted request was invalid.",
            "spending_cap_too_small" => "The credits held are too few for this model and effort. Send it again to let Temno estimate the hold, or choose a lower effort.",
            "hold_confirmation_required" => "This request needs a larger credit hold than Temno takes without asking.",
            // An older gateway reads the automatic hold (0) as an invalid cap; nothing is held or sent either way.
            "hold_unsupported" or "invalid_spending_bounds" => "Hosted chat is updating. Nothing was sent.",
            // WP-254: why a hosted turn ended. Output, context, spending, steps, provider, auth and policy stay distinct.
            "output_exhausted" => "The model used its whole output allowance on reasoning before it answered.",
            "output_truncated" => "The reply was cut off at its output limit.",
            "truncated_tool_call" => "A tool call was cut off at the output limit, so it was not run.",
            "empty_output" => "The model returned no reply.",
            "content_filtered" => "The provider's content filter stopped the reply.",
            "provider_error" => "The provider reported an error partway through the reply.",
            "context_exhausted" or "context_too_large" or "context_limit"
                => "The conversation is too long for another step with this model.",
            "provider_context_rejected" => "The provider rejected the request as longer than its context window.",
            // WP-254: the credits held for a request (an estimate, or a cap an older desktop set) are what ran out.
            "spending_cap_exhausted" or "hold_exhausted" => "The credits held for this request ran out before the work finished.",
            "balance_exhausted" => "The credits your balance allowed for this request ran out before the work finished. Add credits, then send a follow-up.",
            "call_limit" => "The turn reached its step limit.",
            "tool_limit" => "The turn reached a tool-call limit: too many tools for this turn, or more than Temno runs in one step.",
            "no_progress" => "The same tool calls kept returning the same results, so the turn stopped.",
            "deadline" => "The turn reached its time limit.",
            "openrouter_unauthorized" => "The hosted chat provider rejected the service's credentials. This is a service problem, not your account.",
            "provider_policy_rejected" => "The provider's guardrail or usage policy blocked this request.",
            "provider_route_unavailable" => "No provider endpoint currently meets this model's routing and data policy.",
            "provider_rejected" => "The provider rejected the request.",
            "invalid_tool_response" => "The model asked for a tool call Temno could not validate, so it was not run.",
            "unexpected_model" or "unexpected_provider" => "The reply came from a different model or provider than quoted, so it was discarded.",
            "usage_unknown" or "worker_lost" or "interrupted" or "stream_interrupted"
                => "The call was interrupted and its usage could not be confirmed.",
            "quote_expired" => "The quote expired. Send again to get a new quote.",
            "content_expired" => "That hosted turn has expired. Start a new message.",
            "user_cancel" => "Cancelled. Credits already used on this turn can still be charged.",
            "submission_uncertain" => "The hosted call may have started. It will not be billed twice.",
            // WP-393: why a cloud transcription ended without starting, or was refused for its length.
            "claim_timeout" => "Cloud transcription could not start this job in time. Nothing was charged; try again.",
            "audio_too_long" => Temno.Core.Transcription.Pcm16Wav.TooLongMessage,
            _ => status switch
            {
                401 => "Sign in to use hosted credits.",
                402 => "Insufficient credits.",
                429 => "Rate limited. Try again later.",
                503 => "The hosted provider is down.",
                null => "Hosted request failed.",
                _ => $"Hosted request failed (HTTP {status}).",
            },
        };
    }

    public static bool TryParse(string? json, out string? code, out string? message)
    {
        code = null;
        message = null;
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return false;
            var root = doc.RootElement;
            code = ReadString(root, "code") ?? ReadString(root, "error");
            message = ReadString(root, "message")
                      ?? ReadString(root, "errorMessage")
                      ?? ReadString(root, "error_message");
            if (root.TryGetProperty("error", out var errorEl) && errorEl.ValueKind == JsonValueKind.Object)
            {
                code ??= ReadString(errorEl, "code");
                message ??= ReadString(errorEl, "message");
            }

            return !string.IsNullOrEmpty(code) || !string.IsNullOrEmpty(message);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ReadString(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el))
            return null;
        return el.ValueKind == JsonValueKind.String ? el.GetString() : null;
    }
}
