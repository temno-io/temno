using System.Text.Json.Nodes;
using Temno.Core.Models;

namespace Temno.Generation;

/// <summary>
/// Builds tool names and MCP argument payloads for <c>GenerationComposer</c> submissions so
/// every kind (including <see cref="GenerationKind.Upscale"/>) emits only schema-valid keys.
/// </summary>
public static class GenerationComposerSubmission
{
    public static (string Tool, JsonObject Args) BuildHostedMediaSubmit(
        GenerationKind kind,
        string quoteId,
        int maxCredits,
        string modelId,
        string operation,
        string prompt,
        bool acceptsPrompt)
    {
        var tool = kind switch
        {
            GenerationKind.Video => "generate_video",
            GenerationKind.Audio => "generate_audio",
            GenerationKind.Upscale => "upscale_media",
            _ => throw new ArgumentOutOfRangeException(
                nameof(kind), kind, "Hosted media submit supports video, audio, and upscale only."),
        };
        var args = new JsonObject
        {
            ["quoteId"] = quoteId,
            ["maxCredits"] = maxCredits,
            ["model"] = modelId,
            ["operation"] = operation,
        };
        // upscale_media's schema does not take prompt; quote_media already binds any optional prompt.
        // Promptless video/audio operations (dubbing, audio_isolation, etc.) omit prompt as well.
        if (tool != "upscale_media" && acceptsPrompt)
            args["prompt"] = prompt;
        return (tool, args);
    }

    public static (string Tool, JsonObject Args) BuildLegacySubmit(
        GenerationKind kind,
        string modelId,
        string prompt,
        JsonObject settings,
        IReadOnlyList<string> references,
        ClipType? firstReferenceType)
    {
        var trimmedPrompt = prompt.Trim();
        if (trimmedPrompt.Length == 0 && kind != GenerationKind.Upscale)
            throw new InvalidOperationException("Enter a prompt.");

        var args = new JsonObject { ["model"] = modelId };
        if (kind != GenerationKind.Upscale)
            args["prompt"] = trimmedPrompt;

        if ((kind == GenerationKind.Video || kind == GenerationKind.Audio) && settings["duration"] is { } duration)
        {
            if (!int.TryParse(duration.ToString(), out var seconds) || seconds <= 0)
                throw new InvalidOperationException("Duration must be a positive number of seconds.");
            args["duration"] = seconds;
        }
        if ((kind == GenerationKind.Image || kind == GenerationKind.Video) && settings["aspect_ratio"] is { } aspect)
            args["aspectRatio"] = aspect.ToString();
        if (kind == GenerationKind.Audio)
        {
            foreach (var key in new[] { "voice", "lyrics" })
                if (settings[key] is { } value) args[key] = value.DeepClone();
        }
        if (kind == GenerationKind.Video && references.Count > 0)
            args[firstReferenceType == ClipType.image ? "startFrameMediaRef" : "sourceVideoMediaRef"] = references[0];
        if (kind == GenerationKind.Upscale)
        {
            if (references.Count != 1)
                throw new InvalidOperationException("Choose one source to upscale.");
            args["mediaRef"] = references[0];
        }

        var tool = kind switch
        {
            GenerationKind.Image => "generate_image",
            GenerationKind.Video => "generate_video",
            GenerationKind.Audio => "generate_audio",
            _ => "upscale_media",
        };
        return (tool, args);
    }
}
