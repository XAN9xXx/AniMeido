namespace AniMeido.Plugin.AI.Models;

internal sealed record AiProviderDescriptor(
    AiProviderKind Kind,
    string DisplayName,
    string DocumentationUrl,
    bool UsesOpenAiCompatibleApi);

internal static class AiProviderCatalog
{
    private static readonly IReadOnlyDictionary<AiProviderKind, AiProviderDescriptor>
        Descriptors = new AiProviderDescriptor[]
        {
            new(
                AiProviderKind.OpenAI,
                "OpenAI",
                "https://platform.openai.com/docs/",
                false),
            new(
                AiProviderKind.Anthropic,
                "Anthropic",
                "https://docs.anthropic.com/",
                false),
            new(
                AiProviderKind.Gemini,
                "Gemini",
                "https://ai.google.dev/gemini-api/docs",
                false),
            new(
                AiProviderKind.OpenAICompatible,
                "自定义 OpenAI-compatible",
                "https://platform.openai.com/docs/api-reference/chat",
                true),
            new(
                AiProviderKind.DeepSeek,
                "DeepSeek",
                "https://api-docs.deepseek.com/",
                true),
            new(
                AiProviderKind.Qwen,
                "通义千问（阿里云百炼）",
                "https://help.aliyun.com/zh/model-studio/compatibility-of-openai-with-dashscope",
                true),
            new(
                AiProviderKind.Moonshot,
                "Kimi（Moonshot）",
                "https://platform.moonshot.cn/docs/",
                true),
            new(
                AiProviderKind.Zhipu,
                "智谱 GLM",
                "https://open.bigmodel.cn/dev/api",
                true),
            new(
                AiProviderKind.Doubao,
                "豆包（火山方舟）",
                "https://www.volcengine.com/docs/82379",
                true),
        }.ToDictionary(item => item.Kind);

    public static IReadOnlyList<AiProviderDescriptor> All { get; } =
        Descriptors.Values.OrderBy(item => (int)item.Kind).ToList();

    public static AiProviderDescriptor Get(AiProviderKind kind)
        => Descriptors.TryGetValue(kind, out var descriptor)
            ? descriptor
            : throw new NotSupportedException($"不支持 Provider：{kind}");
}
