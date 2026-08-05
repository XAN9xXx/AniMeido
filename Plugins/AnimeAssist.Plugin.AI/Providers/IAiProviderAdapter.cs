using AniMeido.Plugin.AI.Models;

namespace AniMeido.Plugin.AI.Providers;

internal interface IAiProviderAdapter
{
    AiProviderKind Kind { get; }

    AiProviderCapabilities Capabilities { get; }

    Task<AiProviderResult> SendAsync(
        AiProviderRequest request,
        IProgress<string>? progress,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> GetModelsAsync(
        AiSettings settings,
        string apiKey,
        CancellationToken cancellationToken);
}
