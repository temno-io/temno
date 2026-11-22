using System.Text.Json;
using System.Text.Json.Serialization;
using Temno.Core.Models;

namespace Temno.Core.Agent;

public sealed class ChatSession
{
    [JsonIgnore]
    public object SyncRoot { get; } = new();

    [JsonPropertyName("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [JsonPropertyName("title")]
    public string Title { get; set; } = "New chat";

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("messages")]
    public List<AgentMessage> Messages { get; set; } = [];

    [JsonPropertyName("isOpen")]
    public bool IsOpen { get; set; } = true;

    [JsonPropertyName("hosted")]
    public HostedChatMetadata? Hosted { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class AgentMessage
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [JsonPropertyName("role")]
    public AgentMessageRole Role { get; set; }

    [JsonPropertyName("blocks")]
    public List<AgentContentBlock> Blocks { get; set; } = [];

    [JsonPropertyName("mentions")]
    public List<AgentMention> Mentions { get; set; } = [];

    [JsonPropertyName("contextHint")]
    public string? ContextHint { get; set; }

    // Set when Temno wrote the message (for example a hosted turn report), not the model or the user.
    // Such messages are shown but never sent back as model history.
    [JsonPropertyName("origin")]
    public string? Origin { get; set; }

    public static AgentMessage UserText(string text) =>
        new() { Role = AgentMessageRole.user, Blocks = [AgentContentBlock.FromText(text)] };

    public static AgentMessage Assistant(params AgentContentBlock[] blocks) =>
        new() { Role = AgentMessageRole.assistant, Blocks = [.. blocks] };
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AgentMessageRole
{
    user,
    assistant,
    system,
}

public sealed class AgentContentBlock
{
    public const string KindText = "text";
    public const string KindToolUse = "toolUse";
    public const string KindToolResult = "toolResult";

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = KindText;

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("input")]
    public string? Input { get; set; }

    [JsonPropertyName("toolUseId")]
    public string? ToolUseId { get; set; }

    [JsonPropertyName("content")]
    public List<AgentToolResultBlock>? Content { get; set; }

    [JsonPropertyName("isError")]
    public bool? IsError { get; set; }

    public static AgentContentBlock FromText(string text) =>
        new() { Kind = KindText, Text = text };

    public static AgentContentBlock ToolUse(string id, string name, string inputJson) =>
        new() { Kind = KindToolUse, Id = id, Name = name, Input = inputJson };

    public static AgentContentBlock ToolResult(string toolUseId, IReadOnlyList<AgentToolResultBlock> content, bool isError) =>
        new()
        {
            Kind = KindToolResult,
            ToolUseId = toolUseId,
            Content = [.. content],
            IsError = isError,
        };
}

public sealed class AgentToolResultBlock
{
    public const string KindText = "text";
    public const string KindImage = "image";

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = KindText;

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("base64")]
    public string? Base64 { get; set; }

    [JsonPropertyName("mediaType")]
    public string? MediaType { get; set; }

    public static AgentToolResultBlock FromText(string text) =>
        new() { Kind = KindText, Text = text };

    public static AgentToolResultBlock Image(string base64, string mediaType) =>
        new() { Kind = KindImage, Base64 = base64, MediaType = mediaType };
}

public sealed class AgentMention
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("mediaRef")]
    public string? MediaRef { get; set; }

    [JsonPropertyName("type")]
    public ClipType? Type { get; set; }

    [JsonPropertyName("clipId")]
    public string? ClipId { get; set; }

    [JsonPropertyName("timelineRange")]
    public AgentTimelineRangeMention? TimelineRange { get; set; }
}

public sealed class AgentTimelineRangeMention
{
    [JsonPropertyName("startFrame")]
    public int StartFrame { get; set; }

    [JsonPropertyName("endFrame")]
    public int EndFrame { get; set; }

    [JsonPropertyName("durationFrames")]
    public int DurationFrames { get; set; }

    [JsonPropertyName("fps")]
    public int Fps { get; set; }

    [JsonPropertyName("startTimecode")]
    public string StartTimecode { get; set; } = "";

    [JsonPropertyName("endTimecode")]
    public string EndTimecode { get; set; } = "";

    [JsonPropertyName("durationTimecode")]
    public string DurationTimecode { get; set; } = "";

    [JsonPropertyName("rangeSemantics")]
    public string RangeSemantics { get; set; } = "startInclusiveEndExclusive";
}

public sealed class HostedChatMetadata
{
    // Frozen before quoting; a retry never builds a different request from edited UI choices.
    public HostedQuoteRequest? Submission { get; set; }
    public string? AccountId { get; set; }
    public Guid? UserMessageId { get; set; }
    public Guid? AssistantMessageId { get; set; }
    public bool SubmissionAttempted { get; set; }
    public bool CancelRequested { get; set; }
    public List<string> StartedToolIds { get; set; } = [];
    public string? RecoveryError { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("effort")]
    public string? Effort { get; set; }

    [JsonPropertyName("reasoningEnabled")]
    public bool? ReasoningEnabled { get; set; }

    [JsonPropertyName("quoteId")]
    public string? QuoteId { get; set; }

    [JsonPropertyName("turnId")]
    public string? TurnId { get; set; }

    [JsonPropertyName("idempotencyKey")]
    public string? IdempotencyKey { get; set; }

    [JsonPropertyName("lastEventId")]
    public int LastEventId { get; set; }

    // WP-431: this turn's stream subscription key (X-Temno-Stream-Reader), 32 lowercase hex, made before the first open and
    // kept across reconnect and relaunch so a reconnect replaces only its own predecessor. It grants nothing. The scope is
    // a digest of the account, auth session and turn it was made for; when any of them changes the key is made anew.
    [JsonPropertyName("streamReader")]
    public string? StreamReader { get; set; }

    [JsonPropertyName("streamReaderScope")]
    public string? StreamReaderScope { get; set; }

    [JsonPropertyName("executedToolIds")]
    public List<string> ExecutedToolIds { get; set; } = [];

    [JsonPropertyName("toolResults")]
    public Dictionary<string, string> ToolResults { get; set; } = new(StringComparer.Ordinal);

    // WP-436: the Gateway's image rules, frozen when the turn's model takes images. Null sends text only.
    [JsonPropertyName("toolImages")]
    public HostedImagePolicy? ToolImages { get; set; }

    // A cap set by desktops before WP-254, or a hold the user confirmed. Automatic holds leave it null.
    [JsonPropertyName("spendingCapCredits")]
    public long? SpendingCapCredits { get; set; }

    // What the quote held for this turn (WP-254); unused credits return at settlement.
    [JsonPropertyName("holdCredits")]
    public long? HoldCredits { get; set; }

    [JsonPropertyName("callBoundCredits")]
    public long? CallBoundCredits { get; set; }

    [JsonPropertyName("lastChargeCredits")]
    public long? LastChargeCredits { get; set; }

    [JsonPropertyName("lastError")]
    public string? LastError { get; set; }

    [JsonPropertyName("usageUnknown")]
    public bool UsageUnknown { get; set; }

    [JsonPropertyName("terminal")]
    public bool? Terminal { get; set; }

    // WP-254: why work stopped early, whether the model began its closing report, and the app report written.
    [JsonPropertyName("stopReason")]
    public string? StopReason { get; set; }

    [JsonPropertyName("reportStarted")]
    public bool ReportStarted { get; set; }

    [JsonPropertyName("reportMessageId")]
    public Guid? ReportMessageId { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}
