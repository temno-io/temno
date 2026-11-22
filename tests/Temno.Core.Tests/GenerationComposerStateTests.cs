using System.Text.Json.Nodes;
using Temno.Core.Models;
using Temno.Core.Account;
using Temno.Core.Session;
using Temno.Generation;
using Temno.Mcp;
using Xunit;

namespace Temno.Core.Tests;

[Collection("EnvRegistry")]
public sealed class GenerationComposerStateTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Changed_draft_rejects_late_quote_response()
    {
        var draft = new GenerationDraftQuote();
        draft.SetContext(new object(), "owner");
        var revision = draft.Revision;
        draft.Invalidate();
        Assert.False(draft.Accept(revision, "old-quote", 4, Now.AddMinutes(2), Now));
        Assert.Null(draft.QuoteId);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Project_or_account_switch_invalidates_accepted_price(bool projectChanges, bool ownerChanges)
    {
        var project = new object();
        var draft = new GenerationDraftQuote();
        draft.SetContext(project, "first");
        Assert.True(draft.Accept(draft.Revision, "quote", 4, Now.AddMinutes(2), Now));
        draft.SetContext(projectChanges ? new object() : project, ownerChanges ? "second" : "first");
        Assert.False(draft.IsValid(Now));
        Assert.Null(draft.QuoteId);
    }

    [Fact]
    public void Balance_and_token_refresh_for_same_account_preserves_the_quote()
    {
        var project = new object();
        var draft = new GenerationDraftQuote();
        draft.SetContext(project, "owner");
        Assert.True(draft.Accept(draft.Revision, "quote", 9, Now.AddMinutes(2), Now));
        draft.SetContext(project, "owner");
        Assert.True(draft.IsValid(Now.AddSeconds(90)));
        Assert.Equal(9, draft.Credits);
        Assert.False(draft.IsValid(Now.AddMinutes(2)));
    }

    [Fact]
    public void Replacement_lookup_clears_old_expiry_before_capturing_its_revision()
    {
        var draft = new GenerationDraftQuote();
        Assert.True(draft.Accept(draft.Revision, "old", 4, Now.AddSeconds(1), Now));
        var replacementRevision = draft.BeginQuote();
        // The UI expiry tick invalidates only a currently displayed quote.
        if (draft.QuoteId is not null && !draft.IsValid(Now.AddSeconds(2))) draft.Invalidate();
        Assert.True(draft.Accept(replacementRevision, "new", 8, Now.AddMinutes(2), Now.AddSeconds(2)));
        Assert.Equal("new", draft.QuoteId);
    }

    [Theory]
    [InlineData("", 4, 120)]
    [InlineData("quote", -1, 120)]
    [InlineData("quote", 4, 0)]
    public void Invalid_provider_quote_never_enables_submit(string id, int credits, int seconds)
    {
        var draft = new GenerationDraftQuote();
        Assert.False(draft.Accept(draft.Revision, id, credits, Now.AddSeconds(seconds), Now));
        Assert.False(draft.IsValid(Now));
    }

    [Theory]
    [InlineData("quote_image")]
    [InlineData("generate_image")]
    [InlineData("edit_image")]
    [InlineData("generate_video")]
    [InlineData("generate_audio")]
    [InlineData("upscale_media")]
    public void Bound_generation_rejects_a_project_switch_before_any_work(string tool)
    {
        var previous = ProjectHub.ActiveSession;
        var first = new ProjectSession(new ProjectFile());
        var second = new ProjectSession(new ProjectFile());
        try
        {
            ProjectHub.ActiveSession = second;
            var result = ToolExecutor.ExecuteGenerationForProject(first, tool, new JsonObject { ["prompt"] = "draft from first project" });
            Assert.True(result.IsError);
            Assert.Contains("active project changed", result.Content[0].Text);
            Assert.Empty(first.Manifest.Entries);
            Assert.Empty(second.Manifest.Entries);
        }
        finally { ProjectHub.ActiveSession = previous; }
    }

    // WP-329: hosted generation is quote-only, so the panel has no fixed hosted offer: no forced
    // MiniMax parameters, no fixed price table, and a hosted catalog that fails to load is the
    // panel's error state rather than a fall back to list_models. Read from source, like
    // SessionMutationSignalTests, because Temno.App is not referenced here.
    [Fact]
    public void Composer_has_no_hosted_fixed_offer_fallback()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Temno.App")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var files = Directory.GetFiles(Path.Combine(dir!.FullName, "src", "Temno.App", "Controls"), "GenerationComposer*.cs");
        Assert.NotEmpty(files);
        var source = string.Concat(files.Select(File.ReadAllText));
        foreach (var gone in new[] { "768p", "16:9\"; args", "LegacyCredits", "CreditsPerSecond", "CreditsPerImage",
                     "LoadMediaCatalogAsync", "catch (GenerationException) { return null; }" })
            Assert.DoesNotContain(gone, source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ToolExecutor.IsHostedMediaConfigured() ? ToolExecutor.GetMediaCatalogAsync() : null", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Bound_generation_cannot_call_an_unrelated_mutation()
    {
        var result = ToolExecutor.ExecuteGenerationForProject(new ProjectSession(new ProjectFile()), "close_project", new JsonObject());
        Assert.True(result.IsError);
        Assert.Contains("Only generation tools", result.Content[0].Text);
    }

    [Theory]
    [InlineData("quote_image")]
    [InlineData("generate_image")]
    public void Bound_generation_rejects_a_changed_owner_before_transport(string tool)
    {
        var previousProject = ProjectHub.ActiveSession;
        var previousAccount = HostedRuntime.Session;
        var project = new ProjectSession(new ProjectFile());
        try
        {
            ProjectHub.ActiveSession = project;
            HostedRuntime.Session = null;
            var result = ToolExecutor.ExecuteGenerationForProject(project, tool, new JsonObject(), "previous-owner");
            Assert.True(result.IsError);
            Assert.Contains("account changed", result.Content[0].Text);
            Assert.Empty(project.Manifest.Entries);
        }
        finally { ProjectHub.ActiveSession = previousProject; HostedRuntime.Session = previousAccount; }
    }

    [Theory]
    [InlineData(GenerationKind.Upscale, "upscale_image", false, "upscale_media", false)]
    [InlineData(GenerationKind.Upscale, "upscale_video", true, "upscale_media", false)]
    [InlineData(GenerationKind.Video, "text_to_video", true, "generate_video", true)]
    [InlineData(GenerationKind.Video, "lip_sync", false, "generate_video", false)]
    [InlineData(GenerationKind.Audio, "text_to_speech", true, "generate_audio", true)]
    [InlineData(GenerationKind.Audio, "audio_isolation", false, "generate_audio", false)]
    public void Hosted_media_submission_only_emits_schema_valid_arguments(
        GenerationKind kind, string operation, bool acceptsPrompt, string expectedTool, bool expectsPrompt)
    {
        var (tool, args) = GenerationComposerSubmission.BuildHostedMediaSubmit(
            kind, "q-1", 24, "model-1", operation, "upscale prompt", acceptsPrompt);

        Assert.Equal(expectedTool, tool);
        Assert.Equal(expectsPrompt, args.ContainsKey("prompt"));

        var schemaProps = McpToolRegistry.Tools
            .Single(t => t.Name == tool)
            .InputSchema["properties"]!
            .AsObject();
        foreach (var kv in args)
            Assert.True(schemaProps.ContainsKey(kv.Key), $"{tool} schema does not declare argument '{kv.Key}'");
    }

    [Fact]
    public void Legacy_upscale_submission_omits_prompt_and_matches_schema()
    {
        var (tool, args) = GenerationComposerSubmission.BuildLegacySubmit(
            GenerationKind.Upscale,
            "topaz-upscale",
            "upscale the image to higher quality.",
            new JsonObject { ["duration"] = "5", ["aspect_ratio"] = "16:9" },
            ["ASSET-1"],
            ClipType.image);

        Assert.Equal("upscale_media", tool);
        Assert.False(args.ContainsKey("prompt"));
        Assert.False(args.ContainsKey("duration"));
        Assert.False(args.ContainsKey("aspectRatio"));
        Assert.Equal("ASSET-1", args["mediaRef"]?.GetValue<string>());
        Assert.Equal("topaz-upscale", args["model"]?.GetValue<string>());

        var schemaProps = McpToolRegistry.Tools
            .Single(t => t.Name == tool)
            .InputSchema["properties"]!
            .AsObject();
        foreach (var kv in args)
            Assert.True(schemaProps.ContainsKey(kv.Key), $"{tool} schema does not declare argument '{kv.Key}'");
    }

    [Fact]
    public void Hosted_media_submission_rejects_image_kind()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GenerationComposerSubmission.BuildHostedMediaSubmit(
                GenerationKind.Image, "q-1", 10, "flux-schnell", "generate_image", "a cat", acceptsPrompt: true));
    }
}
