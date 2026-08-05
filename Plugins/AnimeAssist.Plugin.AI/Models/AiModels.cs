using AniMeido.Contracts.PersonalAnime;

namespace AniMeido.Plugin.AI.Models;

internal enum AiProviderKind
{
    OpenAI = 0,
    Anthropic = 1,
    Gemini = 2,
    OpenAICompatible = 3,
    DeepSeek = 4,
    Qwen = 5,
    Moonshot = 6,
    Zhipu = 7,
    Doubao = 8,
}

internal enum AiTaskKind
{
    CompareAnime = 0,
    OrganizePlan = 1,
    SummarizeArchive = 2,
    ExplainPreferences = 3,
    ReviewTracking = 4,
}

internal sealed record AiSettings(
    int SchemaVersion,
    AiProviderKind Provider,
    string Model,
    string BaseUrl,
    bool AllowProviderWebTools,
    int TimeoutSeconds,
    int MaximumOutputTokens)
{
    public const int CurrentSchemaVersion = 1;

    public static AiSettings Default => new(
        CurrentSchemaVersion,
        AiProviderKind.OpenAI,
        string.Empty,
        string.Empty,
        false,
        120,
        4096);
}

internal sealed record AiConversation(
    string ConversationId,
    string Title,
    AiTaskKind TaskKind,
    AiProviderKind Provider,
    string Model,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? SnapshotJson,
    int SnapshotRevision);

internal sealed record AiMessage(
    string MessageId,
    string ConversationId,
    string Role,
    string Body,
    DateTimeOffset CreatedAt,
    int InputTokens,
    int OutputTokens,
    string ToolSummary);

internal sealed record AiProviderRequest(
    string SystemPrompt,
    IReadOnlyList<AiMessage> Messages,
    string UserMessage,
    PersonalAnimeContextSnapshot Snapshot,
    bool AllowWebTools,
    AiSettings Settings,
    string ApiKey);

internal sealed record AiProviderResult(
    string Text,
    IReadOnlyList<PersonalAnimeChange> ProposedChanges,
    int InputTokens,
    int OutputTokens,
    string ToolSummary);

internal sealed record AiProviderCapabilities(
    bool SupportsStreaming,
    bool SupportsStructuredChanges,
    bool SupportsWebTools,
    bool SupportsModelListing);

internal sealed record AiTaskDefinition(
    AiTaskKind Kind,
    string Title,
    string Description,
    int MinimumAnimeCount,
    int MaximumAnimeCount,
    PersonalAnimeDataCategory Categories,
    bool AllowsChanges,
    string SystemInstruction);
