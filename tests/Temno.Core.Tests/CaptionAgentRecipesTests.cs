using Temno.Core.Captions;
using Xunit;

namespace Temno.Core.Tests;

public class CaptionAgentRecipesTests
{
    [Fact]
    public void PrimaryCatalogHasTheThreeMacRecipes()
    {
        Assert.Equal(
            [
                ("filler", "Remove filler words"),
                ("names", "Fix names & jargon"),
                ("emoji", "Add emoji"),
            ],
            CaptionAgentRecipes.Primary);
    }

    [Fact]
    public void TranslateLanguagesMatchMac()
    {
        Assert.Equal(
            [
                "Spanish", "French", "German", "Italian", "Portuguese",
                "Japanese", "Korean", "Chinese", "Hindi", "Arabic",
            ],
            CaptionAgentRecipes.TranslateLanguages);
    }

    [Fact]
    public void FillerPromptKeepsMacWordingAndDoesNotSend()
    {
        var prompt = CaptionAgentRecipes.PromptFor(CaptionAgentRecipes.FillerId);
        Assert.StartsWith(CaptionAgentRecipes.Prefix, prompt, StringComparison.Ordinal);
        Assert.Contains("um, uh, er, like, you know", prompt, StringComparison.Ordinal);
        Assert.Contains("keeping each caption's timing unchanged", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("send", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NamesAndEmojiPromptsStayPrefillOnly()
    {
        var names = CaptionAgentRecipes.PromptFor(CaptionAgentRecipes.NamesId);
        Assert.Contains("misspelled names, brand names, or technical jargon", names, StringComparison.Ordinal);
        Assert.StartsWith(CaptionAgentRecipes.Prefix, names, StringComparison.Ordinal);

        var emoji = CaptionAgentRecipes.PromptFor(CaptionAgentRecipes.EmojiId);
        Assert.Contains("add relevant emoji", emoji, StringComparison.Ordinal);
        Assert.StartsWith(CaptionAgentRecipes.Prefix, emoji, StringComparison.Ordinal);
    }

    [Fact]
    public void TranslatePromptNamesTheLanguageAndWhisperEnglishLimit()
    {
        var prompt = CaptionAgentRecipes.PromptFor(CaptionAgentRecipes.TranslateItemId("Spanish"));
        Assert.StartsWith(CaptionAgentRecipes.Prefix, prompt, StringComparison.Ordinal);
        Assert.Contains("translate the captions to Spanish", prompt, StringComparison.Ordinal);
        Assert.Contains(CaptionAgentRecipes.TranslateHonesty.Trim(), prompt, StringComparison.Ordinal);
        Assert.Contains("English Whisper", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownRecipeAndLanguageThrow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CaptionAgentRecipes.PromptFor("not-a-recipe"));
        Assert.Throws<ArgumentOutOfRangeException>(() => CaptionAgentRecipes.Translate("Klingon"));
        Assert.Throws<ArgumentException>(() => CaptionAgentRecipes.PromptFor(""));
    }
}
