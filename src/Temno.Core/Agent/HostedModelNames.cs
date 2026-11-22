namespace Temno.Core.Agent;

/// <summary>Where a saved Settings choice stands against the Gateway's catalog (WP-422).</summary>
public enum HostedChoiceState
{
    /// <summary>No choice is saved: new projects start on the default model.</summary>
    Default,
    /// <summary>The Gateway serves the saved model qualified.</summary>
    Available,
    /// <summary>A catalog is loaded and does not serve the saved model, or serves it unqualified.</summary>
    NotAvailable,
    /// <summary>Signed out or no catalog yet: the saved choice is shown by name until one loads.</summary>
    CatalogUnknown,
}

/// <summary>
/// How the hosted chat picker names the Gateway's models and groups them under the company that makes each one.
/// The creator comes from the OpenRouter model ID's author, which is not always the company hosting the endpoint.
/// </summary>
public static class HostedModelNames
{
    // Picker order of creators; an author not listed follows them, alphabetically.
    private static readonly (string Author, string Creator)[] Creators =
    [
        ("anthropic", "Anthropic"),
        ("openai", "OpenAI"),
        ("google", "Google"),
        ("meta", "Meta"),
        ("moonshotai", "MoonshotAI"),
        ("x-ai", "SpaceXAI"),
    ];

    // Strongest first inside each creator; a model not listed follows them, by ID.
    private static readonly string[] Order =
    [
        "claude-opus-5.5", "claude-opus-5", "claude-sonnet-5.5", "claude-sonnet-5", "claude-haiku-5.5",
        "gpt-6.2-sol", "gpt-6.1-sol", "gpt-6.1-terra", "gpt-6-luna",
        "gemini-4-carbon", "gemini-4-neon", "gemini-4-hydrogen", "gemini-4-argon", "gemini-3.8-flash",
        "muse-spark-1.3",
        "kimi-k3.1-pro",
        "grok-4.7",
    ];

    public static string Label(HostedAgentProfile profile) =>
        KnownLabel(profile.Id) ?? (string.IsNullOrWhiteSpace(profile.Model) ? profile.Id : profile.Model);

    /// <summary>
    /// A model ID's picker name when no catalog entry is at hand, as Settings shows a saved choice before the catalog
    /// loads (WP-422). An ID this build does not name comes back as it is.
    /// </summary>
    public static string LabelFor(string modelId) => KnownLabel(modelId) ?? modelId;

    // Owner, 2026-11-06: every row names its model family, as the other creators' rows do (GPT-6.1 Sol, Gemini 4 Argon, Grok 4.7),
    // so Anthropic's say "Claude" too. The creator's own name is the group heading.
    private static string? KnownLabel(string id) => id switch
    {
        "claude-opus-5.5" => "Claude Opus 5.5",
        "claude-opus-5" => "Claude Opus 5",
        "claude-sonnet-5.5" => "Claude Sonnet 5.5",
        "claude-sonnet-5" => "Claude Sonnet 5",
        "claude-haiku-5.5" => "Claude Haiku 5.5",
        "gpt-6.2-sol" => "GPT-6.2 Sol",
        "gpt-6.1-sol" => "GPT-6.1 Sol",
        "gpt-6.1-terra" => "GPT-6.1 Terra",
        "gpt-6-luna" => "GPT-6 Luna",
        "gemini-4-carbon" => "Gemini 4 Carbon",
        "gemini-4-neon" => "Gemini 4 Neon",
        "gemini-4-hydrogen" => "Gemini 4 Hydrogen",
        "gemini-4-argon" => "Gemini 4 Argon",
        "gemini-3.8-flash" => "Gemini 3.8 Flash",
        "muse-spark-1.3" => "Muse Spark 1.3",
        "kimi-k3.1-pro" => "Kimi K3.1 Pro",
        "grok-4.7" => "Grok 4.7",
        _ => null,
    };

    public static string Creator(HostedAgentProfile profile)
    {
        var author = Author(profile);
        foreach (var (key, creator) in Creators)
            if (string.Equals(author, key, StringComparison.Ordinal))
                return creator;
        return author.Length > 0 ? author : "Other";
    }

    /// <summary>Creators in picker order, each with its models strongest first.</summary>
    public static IReadOnlyList<(string Creator, IReadOnlyList<HostedAgentProfile> Models)> Grouped(IEnumerable<HostedAgentProfile> models) =>
        models
            .GroupBy(Creator, StringComparer.Ordinal)
            .OrderBy(group => CreatorRank(group.First()))
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => (group.Key, (IReadOnlyList<HostedAgentProfile>)group
                .OrderBy(ModelRank)
                .ThenBy(model => model.Id, StringComparer.Ordinal)
                .ToList()))
            .ToList();

    /// <summary>Every model in the order the picker lists it.</summary>
    public static IEnumerable<HostedAgentProfile> InPickerOrder(IEnumerable<HostedAgentProfile> models) =>
        Grouped(models).SelectMany(group => group.Models);

    // Owner, 2026-11-06: a new project's first chat starts on Haiku 5.5. The 250-credit signup grant can start it (a first
    // turn holds about 12 credits), where Opus 5.5 needs about 492 (docs/audits/free-grant-first-turn-20261106.md). A chat
    // keeps the model and effort it was saved with, and a new chat copies the previous one's, so a project you continue
    // keeps the model and effort you last used and this applies to a new project.
    public const string DefaultModelId = "claude-haiku-5.5";

    /// <summary>
    /// Owner, 2026-11-06: a chat with no saved effort starts at High (Haiku 5.5, the default model, at High), or at the
    /// model's own default when it offers no High. Haiku 5.5 at High holds about 13 credits for a first turn against 12
    /// at Medium: its output floor, 16,384 tokens at $0.50 per million, is 2.5 credits to Medium's 1.8.
    /// </summary>
    public static string StartingEffort(HostedAgentProfile profile) =>
        profile.Efforts.FirstOrDefault(e => string.Equals(e, "high", StringComparison.OrdinalIgnoreCase)) ?? profile.DefaultEffort;

    /// <summary>
    /// The model a chat with no saved model starts on: Haiku 5.5 when the Gateway serves it qualified, otherwise the
    /// first qualified model in picker order (a Gateway deployed before Haiku 5.5 was added).
    /// </summary>
    public static HostedAgentProfile? DefaultFor(IEnumerable<HostedAgentProfile> models)
    {
        var ordered = InPickerOrder(models).ToList();
        return ordered.FirstOrDefault(m => m.Qualified && string.Equals(m.Id, DefaultModelId, StringComparison.Ordinal))
            ?? ordered.FirstOrDefault(m => m.Qualified)
            ?? ordered.FirstOrDefault();
    }

    /// <summary>
    /// The effort a chat shows and sends. Its saved effort when the model offers it. Otherwise, for a chat that names no
    /// model (a new project's first chat, <paramref name="chatNamesModel"/> false), the Settings effort when the model
    /// offers it, else <see cref="StartingEffort"/>. A chat that names its model never takes the Settings effort: it
    /// keeps what was last used.
    /// </summary>
    public static string EffortFor(HostedAgentProfile profile, string? savedEffort, bool chatNamesModel, string? choiceEffort)
    {
        if (savedEffort is { Length: > 0 } && profile.Efforts.Contains(savedEffort, StringComparer.OrdinalIgnoreCase))
            return savedEffort;
        if (!chatNamesModel && !string.IsNullOrWhiteSpace(choiceEffort)
            && profile.Efforts.FirstOrDefault(e => string.Equals(e, choiceEffort.Trim(), StringComparison.OrdinalIgnoreCase)) is { } chosen)
            return chosen;
        return StartingEffort(profile);
    }

    /// <summary>A model's row in Settings' picker when it is the default one: "Claude Haiku 5.5 (default)".</summary>
    public static string WithDefaultMark(string label) => label + " (default)";

    /// <summary>
    /// WP-422: the profile a chat runs on. A chat's saved model wins (found by ID, by the Gateway's model string, or
    /// through the Opus 5, Sonnet 5 and GPT-6.1 Sol aliases) and is never replaced by the Settings choice, even when
    /// the catalog no longer lists it: that returns null, as before. A chat with no saved model takes
    /// <see cref="ForChoice"/>.
    /// </summary>
    public static HostedAgentProfile? Pick(IEnumerable<HostedAgentProfile> models, string? savedModelId, string? choiceId)
    {
        var list = models as IReadOnlyCollection<HostedAgentProfile> ?? models.ToList();
        if (savedModelId is not { Length: > 0 } id)
            return ForChoice(list, choiceId);
        return list.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.Ordinal))
            ?? list.FirstOrDefault(m => string.Equals(m.Model, id, StringComparison.Ordinal))
            ?? (id switch
            {
                "claude-opus-5" or "anthropic/claude-opus-5" => list.FirstOrDefault(m => m.Id == "claude-opus-5.5"),
                "claude-sonnet-5" or "anthropic/claude-sonnet-5" => list.FirstOrDefault(m => m.Id == "claude-sonnet-5.5"),
                "gpt-6.1-sol" or "openai/gpt-6.1-sol" => list.FirstOrDefault(m => m.Id == "gpt-6.2-sol"),
                _ => null,
            });
    }

    /// <summary>
    /// WP-422: the model a chat with no saved model starts on, a new project's first chat. The Settings choice when the
    /// Gateway serves it qualified; otherwise <see cref="DefaultFor"/>, so an empty, unqualified or no longer served
    /// choice never leaves a chat without a model.
    /// </summary>
    public static HostedAgentProfile? ForChoice(IEnumerable<HostedAgentProfile> models, string? choiceId)
    {
        var list = models as IReadOnlyCollection<HostedAgentProfile> ?? models.ToList();
        if (!string.IsNullOrWhiteSpace(choiceId)
            && list.FirstOrDefault(m => m.Qualified && string.Equals(m.Id, choiceId, StringComparison.Ordinal)) is { } chosen)
            return chosen;
        return DefaultFor(list);
    }

    /// <summary>What Settings can say about a saved choice: <c>null</c> or an empty list is a catalog that has not loaded.</summary>
    public static HostedChoiceState ChoiceState(IEnumerable<HostedAgentProfile>? models, string? choiceId)
    {
        if (string.IsNullOrWhiteSpace(choiceId)) return HostedChoiceState.Default;
        var list = models?.ToList();
        if (list is null || list.Count == 0) return HostedChoiceState.CatalogUnknown;
        return list.Any(m => m.Qualified && string.Equals(m.Id, choiceId, StringComparison.Ordinal))
            ? HostedChoiceState.Available
            : HostedChoiceState.NotAvailable;
    }

    private static string Author(HostedAgentProfile profile)
    {
        var slash = profile.Model.IndexOf('/');
        return slash > 0 ? profile.Model[..slash] : "";
    }

    private static int CreatorRank(HostedAgentProfile profile)
    {
        var author = Author(profile);
        var index = Array.FindIndex(Creators, c => string.Equals(c.Author, author, StringComparison.Ordinal));
        return index < 0 ? Creators.Length : index;
    }

    private static int ModelRank(HostedAgentProfile profile)
    {
        var index = Array.IndexOf(Order, profile.Id);
        return index < 0 ? Order.Length : index;
    }
}
