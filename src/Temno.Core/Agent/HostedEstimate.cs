using System.Text;
using System.Text.Json.Nodes;

namespace Temno.Core.Agent;

/// <summary>
/// WP-254: the composer's figure before Send, mirroring the gateway's typical-call estimate (about three bytes
/// per token, base input rate, the effort's typical completion). Display only; the quote's figures win.
/// </summary>
public static class HostedEstimate
{
    // The request envelope around messages and tools: model, max_tokens, stream, reasoning and provider routing,
    // plus the cache_control the gateway adds for Claude (WP-329: ~37 bytes top-level, ~62 on the system message).
    public const int EnvelopeBytes = 352;
    private const int ContextOverhead = 2048;
    // The gateway's default typical completion when reasoning is off, for a catalog that does not publish it.
    private const int OffTypicalTokens = 1024;

    public static long StepCall(HostedAgentProfile profile, int typicalTokens, long inputBytes, long creditsPerUsd = 300)
    {
        var tokens = inputBytes / 3m + ContextOverhead;
        var rates = profile.RatesAt(tokens);
        var usd = (tokens * rates.InputUsdPerMillion + typicalTokens * rates.OutputUsdPerMillion) / 1_000_000m;
        return usd <= 0 ? 0 : Math.Max(5, (long)decimal.Ceiling(usd * creditsPerUsd));
    }

    /// <summary>
    /// WP-331: the gateway's cached step. With a prompt cache the first step writes its input at the cache-write rate and
    /// each later step reads the carried-over input at the cache-read rate and writes only the growth.
    /// </summary>
    public static long CachedStepCall(HostedAgentProfile profile, int typicalTokens, long inputBytes, int k,
        int growthBytes = 16384, long creditsPerUsd = 300)
    {
        if (profile.PromptCache is not { } cache)
            return StepCall(profile, typicalTokens, inputBytes + (long)k * growthBytes, creditsPerUsd);
        // Step k sends the first input plus k growths, so it bills at the tier that many tokens reach.
        var rates = profile.RatesAt((inputBytes + (long)k * growthBytes) / 3m + ContextOverhead);
        var write = cache.Ttl == "1h" ? rates.CacheWrite1hUsdPerMillion : rates.CacheWrite5mUsdPerMillion;
        if (write <= 0) write = rates.InputUsdPerMillion;
        var input = k == 0
            ? inputBytes / 3m * write
            : (inputBytes + (long)(k - 1) * growthBytes) / 3m * rates.CacheReadUsdPerMillion + growthBytes / 3m * write;
        var usd = (input + ContextOverhead * rates.InputUsdPerMillion + typicalTokens * rates.OutputUsdPerMillion) / 1_000_000m;
        return usd <= 0 ? 0 : Math.Max(5, (long)decimal.Ceiling(usd * creditsPerUsd));
    }

    /// <summary>A turn without tools is one call; with tools, the policy's likely steps, each on a grown context.</summary>
    public static (long Low, long High) Likely(HostedAgentProfile profile, int typicalTokens, long inputBytes, bool tools,
        int steps = 3, int growthBytes = 16384, long creditsPerUsd = 300)
    {
        long low = CachedStepCall(profile, typicalTokens, inputBytes, 0, growthBytes, creditsPerUsd), high = 0;
        for (var k = 0; k < (tools ? Math.Max(1, steps) : 1); k++)
            high += CachedStepCall(profile, typicalTokens, inputBytes, k, growthBytes, creditsPerUsd);
        return (low, high);
    }

    /// <summary>The quote history's serialized size, as the gateway will measure it.</summary>
    public static long HistoryBytes(IEnumerable<AgentMessage> history) =>
        Encoding.UTF8.GetByteCount(HostedHistory.ToQuoteMessages(history).ToJsonString());

    /// <summary>WP-436: as the gateway measures it for <paramref name="profile"/>: images by their token allowance.</summary>
    public static long HistoryBytes(IEnumerable<AgentMessage> history, HostedAgentProfile? profile)
    {
        var images = profile is { ImageInput: true } ? profile.ImagePolicy : null;
        return HostedImages.MeasuredBytes(HostedHistory.ToQuoteMessages(history, images), images);
    }

    /// <summary>WP-308: the serialized size of the system message every quote now starts with.</summary>
    public static long InstructionsBytes(IEnumerable<string> tools) =>
        Encoding.UTF8.GetByteCount(HostedHistory.InstructionsMessage(tools).ToJsonString());

    /// <summary>The composer's estimate for sending <paramref name="draft"/> now, or null when the catalog cannot price it.</summary>
    public static (long Low, long High)? ForDraft(HostedAgentCatalog? catalog, HostedAgentProfile? profile, string effort,
        bool reasoningEnabled, long historyBytes, string draft, IReadOnlyCollection<string> tools)
    {
        if (catalog?.BillingPolicy is not { EstimatedHolds: true } policy || profile is null
            || profile.InputUsdPerMillion <= 0 || profile.OutputUsdPerMillion <= 0 || profile.Completion is null
            || !profile.Completion.TryGetValue(effort, out var capacity))
            return null;
        var typical = reasoningEnabled ? capacity.Typical : profile.CompletionOff?.Typical ?? Math.Min(capacity.Typical, OffTypicalTokens);
        long bytes = historyBytes + EnvelopeBytes + InstructionsBytes(tools) + 1;
        if (!string.IsNullOrWhiteSpace(draft))
            bytes += Encoding.UTF8.GetByteCount(new JsonObject { ["role"] = "user", ["content"] = draft.Trim() }.ToJsonString()) + 1;
        foreach (var tool in tools)
            bytes += (catalog.ToolSchemaBytes?.GetValueOrDefault(tool) ?? 0) + 1;
        return Likely(profile, typical, bytes, tools.Count > 0, policy.LikelySteps, policy.StepGrowthBytes, policy.CreditsPerUpstreamUsd);
    }

    /// <summary>"60–205": the low end rounded down and the high end up, to 5 credits, so it reads as an estimate.</summary>
    public static string Range(long low, long high)
    {
        long down = Math.Max(0, low / 5 * 5), up = (high + 4) / 5 * 5;
        return down >= up ? up.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)
            : $"{down.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)}–{up.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)}";
    }
}
