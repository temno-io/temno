using Temno.Generation;

namespace Temno.App.Services;

public sealed record GenerationPanelRequest(GenerationKind Kind = GenerationKind.Image,
    string? MediaRef = null, string? Operation = null, string? Prompt = null);

public static class GenerationPanelRequests
{
    public static event Action<GenerationPanelRequest>? Requested;
    public static void Open(GenerationPanelRequest request) => Requested?.Invoke(request);
}
