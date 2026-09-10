using AniMeido.Contracts.Models;
using AniMeido.Contracts.PersonalAnime;
using AniMeido.Plugin.AI.Models;
using System.Text.Json;

namespace AniMeido.Plugin.AI.Services;

internal sealed class AiTaskCoordinator
{
    private const int MaximumUserTurns = 20;
    private const string ChangePolicy = """
        你是 AniMeido 的受约束领域助手。只能基于用户明确授权的数据回答。
        严格区分本地事实、算法推断和你的解释；不得声称执行了任何变更。
        需要写回时只能调用 propose_anime_changes，变更由用户稍后逐项确认。
        不得提出评分修改、删除既有档案、自动完成整部动画或创建提醒。
        """;
    private static readonly JsonSerializerOptions JsonOptions = new(
        JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private static readonly IReadOnlyDictionary<AiTaskKind, AiTaskDefinition>
        Definitions = BuildDefinitions();
    private readonly IPersonalAnimeDataGateway _gateway;
    private readonly AiSettingsStore _settingsStore;
    private readonly DpapiSecretStore _secretStore;
    private readonly ConversationStore _conversations;
    private readonly AiProviderRouter _providers;

    public AiTaskCoordinator(
        IPersonalAnimeDataGateway gateway,
        AiSettingsStore settingsStore,
        DpapiSecretStore secretStore,
        ConversationStore conversations,
        AiProviderRouter providers)
    {
        _gateway = gateway;
        _settingsStore = settingsStore;
        _secretStore = secretStore;
        _conversations = conversations;
        _providers = providers;
    }

    public IReadOnlyCollection<AiTaskDefinition> TaskDefinitions =>
        Definitions.Values.ToArray();

    public Task<AiSettings> GetSettingsAsync(
        CancellationToken cancellationToken = default)
        => _settingsStore.LoadAsync(cancellationToken);

    public Task<IReadOnlyList<AiConversation>> GetConversationsAsync(
        string? search = null,
        CancellationToken cancellationToken = default)
        => _conversations.GetConversationsAsync(search, cancellationToken);

    public Task<IReadOnlyList<AiMessage>> GetMessagesAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
        => _conversations.GetMessagesAsync(conversationId, cancellationToken);

    public Task RenameConversationAsync(
        string conversationId,
        string title,
        CancellationToken cancellationToken = default)
        => _conversations.RenameConversationAsync(
            conversationId,
            title,
            cancellationToken);

    public Task DeleteConversationAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
        => _conversations.DeleteConversationAsync(
            conversationId,
            cancellationToken);

    public Task ExportAsync(
        string path,
        CancellationToken cancellationToken = default)
        => _conversations.ExportAsync(path, cancellationToken);

    public Task ImportAsync(
        string path,
        CancellationToken cancellationToken = default)
        => _conversations.ImportAsync(path, cancellationToken);

    public static PersonalAnimeContextSnapshot? ReadSnapshot(
        AiConversation conversation)
        => string.IsNullOrWhiteSpace(conversation.SnapshotJson)
            ? null
            : JsonSerializer.Deserialize<PersonalAnimeContextSnapshot>(
                conversation.SnapshotJson,
                JsonOptions);

    public static IReadOnlyList<PersonalAnimeChange> ReadProposedChanges(
        AiMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.ToolSummary))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(message.ToolSummary);
            return document.RootElement.TryGetProperty(
                    "proposedChanges",
                    out var proposals)
                ? proposals.Deserialize<List<PersonalAnimeChange>>(JsonOptions)
                    ?? []
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public Task<IReadOnlyList<PersonalAnimeSelectionItem>> QueryAnimeAsync(
        string? search,
        CancellationToken cancellationToken = default)
        => _gateway.QuerySelectionAsync(
            new PersonalAnimeSelectionQuery(search, Limit: 200),
            cancellationToken);

    public async Task<AiConversation> CreateConversationAsync(
        AiTaskKind task,
        CancellationToken cancellationToken = default)
    {
        var settings = await _settingsStore.LoadAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(settings.Model))
        {
            throw new InvalidOperationException("请先在 AI 插件设置中选择模型。");
        }

        var definition = Definitions[task];
        return await _conversations.CreateConversationAsync(
            task,
            definition.Title,
            settings,
            cancellationToken);
    }

    public async Task<PersonalAnimeContextSnapshot> RefreshSnapshotAsync(
        AiConversation conversation,
        IReadOnlyList<int> animeIds,
        CancellationToken cancellationToken = default)
    {
        var definition = Definitions[conversation.TaskKind];
        if (animeIds.Count < definition.MinimumAnimeCount
            || animeIds.Count > definition.MaximumAnimeCount)
        {
            throw new InvalidOperationException(
                $"{definition.Title}需要选择 {definition.MinimumAnimeCount}–{definition.MaximumAnimeCount} 部番剧。");
        }

        var snapshot = await _gateway.BuildContextAsync(
            new PersonalAnimeContextRequest(
                definition.Title,
                animeIds.Distinct().ToList(),
                definition.Categories),
            cancellationToken);
        await _conversations.SaveSnapshotAsync(
            conversation.ConversationId,
            JsonSerializer.Serialize(snapshot, JsonOptions),
            cancellationToken);
        return snapshot;
    }

    public async Task<AiProviderResult> SendAsync(
        AiConversation conversation,
        string userMessage,
        IProgress<string>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userMessage);
        var current = await _conversations.GetConversationAsync(
            conversation.ConversationId,
            cancellationToken)
            ?? throw new InvalidOperationException("会话已不存在。");
        if (string.IsNullOrWhiteSpace(current.SnapshotJson))
        {
            throw new InvalidOperationException("请先选择数据并生成授权快照。");
        }

        var messages = await _conversations.GetMessagesAsync(
            current.ConversationId,
            cancellationToken);
        if (messages.Count(message => message.Role == "user") >= MaximumUserTurns)
        {
            throw new InvalidOperationException(
                "此会话已达到 20 个用户轮次，请创建新会话。");
        }

        var snapshot = JsonSerializer.Deserialize<PersonalAnimeContextSnapshot>(
            current.SnapshotJson,
            JsonOptions)
            ?? throw new InvalidDataException("授权快照已损坏，请刷新任务数据。");
        var settings = await _settingsStore.LoadAsync(cancellationToken);
        if (settings.Provider != current.Provider
            || !string.Equals(settings.Model, current.Model, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "此会话固定使用创建时的 Provider 与模型。当前设置已变化，请切回原配置或新建会话。");
        }

        var apiKey = _secretStore.LoadApiKey(settings.Provider);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("请先保存 Provider API Key。");
        }

        var definition = Definitions[current.TaskKind];
        var request = new AiProviderRequest(
            string.Join(Environment.NewLine, ChangePolicy, definition.SystemInstruction),
            messages,
            userMessage.Trim(),
            snapshot,
            settings.AllowProviderWebTools,
            settings,
            apiKey);
        var user = new AiMessage(
            Guid.NewGuid().ToString("N"),
            current.ConversationId,
            "user",
            userMessage.Trim(),
            DateTimeOffset.UtcNow,
            0,
            0,
            string.Empty);
        AiProviderResult result;
        try
        {
            result = await _providers.Get(settings.Provider).SendAsync(
                request,
                progress,
                cancellationToken);
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Provider 请求超过 {settings.TimeoutSeconds} 秒。可在设置中调整超时。");
        }
        var assistant = new AiMessage(
                Guid.NewGuid().ToString("N"),
                current.ConversationId,
                "assistant",
                result.Text,
                DateTimeOffset.UtcNow,
                result.InputTokens,
                result.OutputTokens,
                JsonSerializer.Serialize(new
                {
                    summary = result.ToolSummary,
                    proposedChanges = result.ProposedChanges,
                }, JsonOptions));
        await _conversations.AddTurnAsync(
            user,
            assistant,
            cancellationToken);
        return result;
    }

    public Task<PersonalAnimeChangeApplyResult> ApplyChangesAsync(
        AiConversation conversation,
        IReadOnlyList<PersonalAnimeChange> selectedChanges,
        CancellationToken cancellationToken = default)
    {
        var allowed = conversation.TaskKind switch
        {
            AiTaskKind.OrganizePlan or AiTaskKind.ReviewTracking =>
                new[]
                {
                    PersonalAnimeChangeKind.SetTrackingStatus,
                    PersonalAnimeChangeKind.UpsertPlan,
                },
            AiTaskKind.SummarizeArchive =>
                new[]
                {
                    PersonalAnimeChangeKind.ReplaceArchiveSummary,
                    PersonalAnimeChangeKind.AppendArchiveEntry,
                },
            _ => [],
        };
        if (selectedChanges.Any(change => !allowed.Contains(change.Kind)
            || change.TrackingStatus == AnimeTrackingStatus.Completed))
        {
            throw new InvalidOperationException(
                "当前任务包含不允许的写回类型，或试图将整部动画标记为已完成。");
        }

        return _gateway.ApplyChangesAsync(
            new PersonalAnimeChangeSet(
                "AniMeido.Plugin.AI",
                selectedChanges),
            cancellationToken);
    }

    public async Task<string> BuildAuthorizationPreviewAsync(
        AiConversation conversation,
        PersonalAnimeContextSnapshot snapshot,
        string userMessage,
        bool allowWebTools,
        CancellationToken cancellationToken = default)
    {
        var history = await _conversations.GetMessagesAsync(
            conversation.ConversationId,
            cancellationToken);
        return JsonSerializer.Serialize(new
        {
            task = Definitions[conversation.TaskKind].Title,
            provider = conversation.Provider.ToString(),
            conversation.Model,
            dataFields = Definitions[conversation.TaskKind].Categories.ToString(),
            itemCount = snapshot.Items.Count,
            webTools = allowWebTools
                ? conversation.Provider switch
                {
                    AiProviderKind.OpenAI => "OpenAI web_search_preview",
                    AiProviderKind.Anthropic => "Anthropic web_search",
                    AiProviderKind.Gemini => "Gemini googleSearch",
                    _ => "不兼容（发送将被拒绝）",
                }
                : "关闭",
            frozenSnapshot = snapshot,
            conversationHistory = history.Select(item => new
            {
                item.Role,
                item.Body,
            }),
            userMessage,
        }, JsonOptions);
    }

    private static IReadOnlyDictionary<AiTaskKind, AiTaskDefinition>
        BuildDefinitions()
    {
        var common = PersonalAnimeDataCategory.PublicMetadata
            | PersonalAnimeDataCategory.Tracking
            | PersonalAnimeDataCategory.PlansAndProgress
            | PersonalAnimeDataCategory.PersonalRating;
        return new[]
        {
            new AiTaskDefinition(
                AiTaskKind.CompareAnime,
                "选番对比",
                "比较 2–5 部作品并给出下一部观看建议。",
                2,
                5,
                common | PersonalAnimeDataCategory.RecommendationProfile,
                false,
                "比较题材、制作信息、偏好匹配、预计时间投入；输出排序、优势、风险和适合场景。只读。"),
            new AiTaskDefinition(
                AiTaskKind.OrganizePlan,
                "补番计划整理",
                "按时间预算整理补番顺序与目标日期。",
                1,
                30,
                common | PersonalAnimeDataCategory.BrowseSummary,
                true,
                "可提出追番状态、计划优先级和目标日期变更；不得创建提醒。"),
            new AiTaskDefinition(
                AiTaskKind.SummarizeArchive,
                "档案与年度总结",
                "总结档案、感想和观看记录。",
                1,
                20,
                common | PersonalAnimeDataCategory.ArchiveTextAndHistory,
                true,
                "可提出替换档案概要或追加一条观看感想；不得修改评分或删除内容。"),
            new AiTaskDefinition(
                AiTaskKind.ExplainPreferences,
                "偏好画像解读",
                "解释本地偏好画像与收藏 Tag。",
                0,
                20,
                common | PersonalAnimeDataCategory.SavedBangumiTags
                    | PersonalAnimeDataCategory.RecommendationProfile,
                false,
                "区分本地事实、P6 算法推断和 AI 解释；不得反向修改偏好权重。"),
            new AiTaskDefinition(
                AiTaskKind.ReviewTracking,
                "追番状态与计划整理",
                "检查状态、计划、进度和近期活动的不一致。",
                1,
                50,
                common | PersonalAnimeDataCategory.BrowseSummary,
                true,
                "可提出追番状态、计划优先级和目标日期变更；不得自动标记整部完成。"),
        }.ToDictionary(item => item.Kind);
    }
}
