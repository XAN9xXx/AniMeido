using AniMeido.Plugin.AI.Models;
using AniMeido.Plugin.AI.Providers;

namespace AniMeido.Plugin.AI.Services;

internal sealed class AiProviderRouter
{
    private readonly IReadOnlyDictionary<AiProviderKind, IAiProviderAdapter>
        _adapters;
    private readonly OpenAiCompatibleProviderAdapter _compatible;

    public AiProviderRouter(
        OpenAiProviderAdapter openAi,
        AnthropicProviderAdapter anthropic,
        GeminiProviderAdapter gemini,
        OpenAiCompatibleProviderAdapter compatible)
    {
        _compatible = compatible;
        _adapters = new IAiProviderAdapter[]
        {
            openAi,
            anthropic,
            gemini,
            compatible,
        }.ToDictionary(adapter => adapter.Kind);
    }

    public IAiProviderAdapter Get(AiProviderKind kind)
        => _adapters.TryGetValue(kind, out var adapter)
            ? adapter
            : AiProviderCatalog.Get(kind).UsesOpenAiCompatibleApi
                ? _compatible
            : throw new NotSupportedException($"不支持 Provider：{kind}");
}
