using System.Text.Json.Nodes;
using Temno.Generation;
using Xunit;

namespace Temno.Core.Tests;

public sealed class HostedMediaContractTests
{
    private static HostedMediaCatalog Catalog(bool configured = true, bool accept = true,
        bool qualifier = false, List<string>? enabled = null) => new()
    {
        Version = "media-v1",
        DevelopmentPrices = true,
        Configured = configured,
        AcceptJobs = accept,
        QualificationOwner = qualifier,
        EnabledOperations = enabled ?? [],
        Models =
        [
            new HostedMediaProfile
            {
                Id = "seedvr",
                DisplayName = "SeedVR2",
                Family = "SeedVR",
                Variant = "2",
                Category = "upscale",
                Route = "fal",
                Operations =
                [
                    new HostedMediaOperationProfile
                    {
                        Operation = HostedMediaOperations.UpscaleImage,
                        Available = true,
                        Inputs = [new HostedMediaInputSlot { Role = "reference_image", Kinds = ["image"], Min = 1, Max = 1 }],
                        MaxOutputs = 1,
                        PricingUnit = "per_mp",
                        PromptRequired = false,
                        PromptMaxLength = 0,
                    },
                ],
            },
            new HostedMediaProfile
            {
                Id = "veo-3.1",
                DisplayName = "Veo 3.1",
                Category = "video",
                Route = "google",
                Operations =
                [
                    new HostedMediaOperationProfile
                    {
                        Operation = HostedMediaOperations.TextToVideo,
                        Available = false,
                        UnavailableReason = "Google-direct lane.",
                    },
                ],
            },
        ],
    };

    [Fact]
    public void Availability_mirrors_the_server_gate()
    {
        var catalog = Catalog();
        var seedvr = catalog.Get("seedvr")!;
        // Not allowlisted and not a qualifier: honest unavailable.
        Assert.False(catalog.IsAvailable(seedvr, HostedMediaOperations.UpscaleImage));
        Assert.Equal("This operation is not enabled on the gateway yet.",
            catalog.UnavailabilityReason(seedvr, HostedMediaOperations.UpscaleImage));
        // Allowlisted: available.
        catalog.EnabledOperations.Add("seedvr:upscale_image");
        Assert.True(catalog.IsAvailable(seedvr, HostedMediaOperations.UpscaleImage));
        Assert.Null(catalog.UnavailabilityReason(seedvr, HostedMediaOperations.UpscaleImage));
        // Unknown operation and gateway-down states stay honest.
        Assert.Equal("This model has no dubbing operation.",
            catalog.UnavailabilityReason(seedvr, HostedMediaOperations.Dubbing));
        Assert.False(Catalog(configured: false).IsAvailable(seedvr, HostedMediaOperations.UpscaleImage));
        // Unavailable ops surface the server reason, never a generic veil.
        var veo = catalog.Get("veo-3.1")!;
        Assert.Equal("Google-direct lane.",
            catalog.UnavailabilityReason(veo, HostedMediaOperations.TextToVideo));
    }

    [Fact]
    public void Quote_matching_is_role_order_and_settings_sensitive()
    {
        var id = Guid.NewGuid();
        var request = new HostedMediaQuoteRequest
        {
            Model = "SeedVR",
            Operation = HostedMediaOperations.UpscaleImage,
            Prompt = "",
            Settings = new JsonObject { ["upscale_mode"] = "factor" },
            Inputs = [new HostedMediaInputRef { Role = "reference_image", AssetId = id }],
        };
        var quote = new HostedMediaQuote
        {
            QuoteId = "q",
            Model = "seedvr",
            Operation = HostedMediaOperations.UpscaleImage,
            Settings = new JsonObject { ["upscale_mode"] = "factor" },
            Inputs = [new HostedMediaInputRef { Role = "reference_image", AssetId = id }],
        };
        Assert.True(quote.Matches(request));
        // Role swap invalidates even with identical asset ids.
        var swapped = new HostedMediaQuoteRequest
        {
            Model = "seedvr",
            Operation = HostedMediaOperations.UpscaleImage,
            Settings = new JsonObject { ["upscale_mode"] = "factor" },
            Inputs = [new HostedMediaInputRef { Role = "first_frame", AssetId = id }],
        };
        Assert.False(quote.Matches(swapped));
        // Settings drift invalidates.
        swapped.Inputs = quote.Inputs;
        swapped.Settings = new JsonObject { ["upscale_mode"] = "target" };
        Assert.False(quote.Matches(swapped));
    }

    [Fact]
    public void Client_side_media_guards_match_server_bounds()
    {
        Assert.Equal("image", HostedMediaClient.KindFor("image/png"));
        Assert.Equal("video", HostedMediaClient.KindFor("video/quicktime"));
        Assert.Equal("audio", HostedMediaClient.KindFor("audio/flac"));
        Assert.Equal("audio", HostedMediaClient.KindFor("audio/mp4"));
        Assert.Equal("audio", HostedMediaClient.KindFor("audio/aac"));
        Assert.Throws<GenerationException>(() => HostedMediaClient.KindFor("video/avi"));
        Assert.Equal(HostedMediaClient.MaxImageBytes, HostedMediaClient.MaxBytesFor("image"));
        var job = new HostedMediaJob { Id = "j", Status = "running" };
        Assert.False(job.IsTerminal);
        Assert.Equal(BackendGenerationStatus.Running, job.ToBackendJob().Status);
        job.Status = "succeeded";
        job.Error = "stale";
        Assert.True(job.IsTerminal);
        Assert.Equal(BackendGenerationStatus.Succeeded, job.ToBackendJob().Status);
        Assert.Equal(16, HostedMediaOperations.All.Length);
        Assert.DoesNotContain("generate_image", HostedMediaOperations.All);
    }

    [Fact]
    public void Integer_duration_ranges_become_dropdown_choices()
    {
        // Gemini Omni Flash 1.1, as the gateway's DurInt(3, 10, "8") sends it.
        var omni = new HostedMediaSettingOption
        {
            Name = "duration", DefaultValue = "8",
            Description = "Integer 3..10 (seconds of output). Explicit value required; the quote binds it.",
        };
        Assert.Equal(["3", "4", "5", "6", "7", "8", "9", "10"], omni.ChoiceValues());
        Assert.Empty(omni.AllowedValues);

        // A priced set passes through untouched.
        var kling = new HostedMediaSettingOption { Name = "duration", AllowedValues = ["3", "5"], Description = "Output length in seconds." };
        Assert.Same(kling.AllowedValues, kling.ChoiceValues());

        // Ranges too long for a list, other integer settings and unparseable text stay free fields.
        var music = new HostedMediaSettingOption { Name = "duration", Description = "Integer 3..600 (seconds of output)." };
        var safety = new HostedMediaSettingOption { Name = "safety_tolerance", Description = "Integer 0..4. Video-edit only." };
        var optional = new HostedMediaSettingOption { Name = "duration", DefaultValue = "(omitted)", Description = "Optional seconds, 1..600." };
        Assert.Empty(music.ChoiceValues());
        Assert.Empty(safety.ChoiceValues());
        Assert.Empty(optional.ChoiceValues());
        Assert.Equal(29, new HostedMediaSettingOption { Name = "duration", Description = "Integer 2..30 (seconds of output)." }.ChoiceValues().Count);
    }
}
