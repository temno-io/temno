using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Temno.Generation;

public sealed class GenerationException : InvalidOperationException
{
    public GenerationException(string message, int? statusCode = null, string? code = null)
        : base(message)
    {
        StatusCode = statusCode;
        Code = code;
    }

    public int? StatusCode { get; }
    public string? Code { get; }
}

/// <summary>
/// Maps HTTP 401/402 and Convex error envelopes to user-visible generation failures.
/// Never used as a signal to fall back to FakeGenerationClient.
/// </summary>
public static class GenerationHttpErrors
{
    public static readonly IReadOnlySet<string> KnownCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "insufficient_credits",
        "subscription_required",
        "plan_required",
        "rate_limited",
        "unauthenticated",
        "authentication_required",
        "invalid_params",
        "not_configured",
        "generation_disabled",
        "dependency_unavailable",
        "artifact_store_unavailable",
        "openrouter_unfunded",
        "account_suspended",
        "already_rendering",
    };

    public static async Task ThrowIfUnsuccessfulAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken = default)
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

    public static void ThrowIfErrorPayload(string? json)
    {
        if (!TryParsePayload(json, out var code, out var message))
            return;
        if (string.IsNullOrEmpty(code) || !KnownCodes.Contains(code))
            return;
        throw new GenerationException(Describe(status: null, code, message), code: code);
    }

    public static GenerationException FromHttp(int status, string? body)
    {
        TryParsePayload(body, out var code, out var message);
        return new GenerationException(Describe(status, code, message), status, code);
    }

    public static string Describe(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        if (ex is GenerationException gen)
            return gen.Message;
        if (ex is TimeoutException)
            return "Generation timed out.";
        if (ex is OperationCanceledException)
            return "Cancelled";
        if (ex is HttpRequestException http)
            return string.IsNullOrWhiteSpace(http.Message)
                ? "Generation request failed."
                : http.Message;
        return string.IsNullOrWhiteSpace(ex.Message) ? "Generation failed." : ex.Message;
    }

    public static string Describe(int? status, string? code, string? message)
    {
        var trimmed = string.IsNullOrWhiteSpace(message) ? "" : message.Trim();
        if (trimmed.Length > 500)
            trimmed = trimmed[..500];

        var normalized = string.IsNullOrWhiteSpace(code) ? "" : code.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(trimmed))
            return trimmed;

        return normalized switch
        {
            "insufficient_credits" => "Insufficient credits. Open Account and choose a plan.",
            "subscription_required" => "Subscription required.",
            "plan_required" => "Plan required.",
            "rate_limited" => "Rate limited. Try again later.",
            "unauthenticated" or "authentication_required" => "Unauthorized. Check TEMNO_API_KEY in Settings or the environment, or sign in for hosted credits.",
            "invalid_params" => "Invalid generation parameters.",
            "not_configured" or "generation_disabled" => "Hosted generation is not configured.",
            "dependency_unavailable" or "artifact_store_unavailable" or "openrouter_unfunded" => "The hosted provider is down.",
            "account_suspended" => "This account is suspended. Hosted generation is off.",
            "already_rendering" => "Rendering — can't cancel.",
            _ => status switch
            {
                401 => "Unauthorized (HTTP 401). Check TEMNO_API_KEY in Settings or the environment, or sign in for hosted credits.",
                402 => "Payment required (HTTP 402).",
                429 => "Rate limited. Try again later.",
                503 => "The hosted provider is down.",
                null => "Generation failed.",
                _ => $"Generation request failed (HTTP {status}).",
            },
        };
    }

    public static bool TryParsePayload(string? json, out string? code, out string? message)
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
            return TryReadErrorObject(doc.RootElement, out code, out message);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadErrorObject(JsonElement root, out string? code, out string? message)
    {
        code = ReadString(root, "code");
        message = ReadString(root, "message")
                  ?? ReadString(root, "errorMessage")
                  ?? ReadString(root, "error_message");

        if (root.TryGetProperty("error", out var errorEl))
        {
            if (errorEl.ValueKind == JsonValueKind.String)
                code ??= errorEl.GetString();
            else if (errorEl.ValueKind == JsonValueKind.Object)
            {
                code ??= ReadString(errorEl, "code");
                message ??= ReadString(errorEl, "message")
                            ?? ReadString(errorEl, "errorMessage");
            }
        }

        if (root.TryGetProperty("value", out var valueEl) && valueEl.ValueKind == JsonValueKind.Object)
        {
            code ??= ReadString(valueEl, "code");
            message ??= ReadString(valueEl, "message")
                        ?? ReadString(valueEl, "errorMessage");
            if (valueEl.TryGetProperty("error", out var nested) && nested.ValueKind == JsonValueKind.Object)
            {
                code ??= ReadString(nested, "code");
                message ??= ReadString(nested, "message");
            }
        }

        return !string.IsNullOrEmpty(code) || !string.IsNullOrEmpty(message);
    }

    private static string? ReadString(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el))
            return null;
        return el.ValueKind == JsonValueKind.String ? el.GetString() : el.ToString();
    }
}
