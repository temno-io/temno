namespace Temno.Core.Captions;

/// <summary>
/// Mac <c>CaptionTab.agentMenu</c> prompt catalog. The Captions footer dropdown prefills chat
/// from these strings; it does not send.
/// </summary>
public static class CaptionAgentRecipes
{
    public const string MenuTitle = "Agent Mode";

    public const string MenuHelp =
        "Let Agent create captions for you. Choose a predefined task, or ask Agent in the chat.";

    public const string TranslateLabel = "Translate";

    /// <summary>Mac <c>captionTask</c> prefix. Recipes append the per-item clause.</summary>
    public const string Prefix =
        "If the timeline has no captions yet, transcribe the spoken audio and add captions on word boundaries first. Then ";

    /// <summary>
    /// Packaged Whisper is English-only for Generate. Translate works on existing caption text
    /// through the agent, not through a second transcription language.
    /// </summary>
    public const string TranslateHonesty =
        " On-device Generate uses English Whisper only; this recipe translates existing caption text via the agent.";

    public const string FillerId = "filler";
    public const string NamesId = "names";
    public const string EmojiId = "emoji";
    public const string TranslateIdPrefix = "translate:";

    public const string FillerLabel = "Remove filler words";
    public const string NamesLabel = "Fix names & jargon";
    public const string EmojiLabel = "Add emoji";

    public const string FillerTask =
        "remove filler words (um, uh, er, like, you know) from the captions, keeping each caption's timing unchanged.";

    public const string NamesTask =
        "fix any misspelled names, brand names, or technical jargon in the captions using the surrounding context, keeping timing unchanged.";

    public const string EmojiTask =
        "add relevant emoji to the captions, keeping the text and timing otherwise unchanged.";

    /// <summary>Mac <c>CaptionTab.translateLanguages</c>.</summary>
    public static readonly string[] TranslateLanguages =
    [
        "Spanish", "French", "German", "Italian", "Portuguese",
        "Japanese", "Korean", "Chinese", "Hindi", "Arabic",
    ];

    public static IReadOnlyList<(string Id, string Label)> Primary { get; } =
    [
        (FillerId, FillerLabel),
        (NamesId, NamesLabel),
        (EmojiId, EmojiLabel),
    ];

    public static string TranslateItemId(string language) => TranslateIdPrefix + language;

    public static string PromptFor(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Recipe id is required.", nameof(id));

        if (id.StartsWith(TranslateIdPrefix, StringComparison.Ordinal))
            return Translate(id[TranslateIdPrefix.Length..]);

        return id switch
        {
            FillerId => Compose(FillerTask),
            NamesId => Compose(NamesTask),
            EmojiId => Compose(EmojiTask),
            _ => throw new ArgumentOutOfRangeException(nameof(id), id, "Unknown caption agent recipe."),
        };
    }

    public static string Translate(string language)
    {
        if (Array.IndexOf(TranslateLanguages, language) < 0)
            throw new ArgumentOutOfRangeException(nameof(language), language, "Unknown translate language.");
        return Compose($"translate the captions to {language}, keeping each caption's timing unchanged.")
            + TranslateHonesty;
    }

    public static string Compose(string task) => Prefix + task;
}
