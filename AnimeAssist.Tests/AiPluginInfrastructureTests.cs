using AniMeido.Contracts.PersonalAnime;
using AniMeido.Plugin.AI.Models;
using AniMeido.Plugin.AI.Providers;
using AniMeido.Plugin.AI.Services;
using System.Net;
using System.Text;

namespace AniMeido.Tests;

public sealed class AiPluginInfrastructureTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "AniMeido-AI-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void ProviderParser_ExtractsStructuredChangesAndHidesEnvelope()
    {
        const string text = """
            建议先整理计划。
            <ani-changes>{"changes":[{"changeId":"change-1","kind":1,"animeId":96,"title":"Test","reason":"时间更合适","planPriority":2}]}</ani-changes>
            """;

        var result = ProviderResponseParser.Parse(text);

        Assert.Equal("建议先整理计划。", result.Text);
        var change = Assert.Single(result.Changes);
        Assert.Equal("change-1", change.ChangeId);
        Assert.Equal(PersonalAnimeChangeKind.UpsertPlan, change.Kind);
        Assert.Equal(2, change.PlanPriority);
    }

    [Fact]
    public void ProviderParser_RejectsMalformedToolPayload()
    {
        var result = ProviderResponseParser.Parse(
            "普通回答",
            ["{not-json"]);

        Assert.Equal("普通回答", result.Text);
        Assert.Empty(result.Changes);
    }

    [Fact]
    public void TaskCoordinator_RestoresPersistedChangeProposals()
    {
        var message = new AiMessage(
            "message-1",
            "conversation-1",
            "assistant",
            "建议整理计划。",
            DateTimeOffset.UtcNow,
            10,
            5,
            """
                {"summary":"结构化变更提案","proposedChanges":[{"changeId":"change-1","kind":1,"animeId":96,"title":"Test","reason":"时间更合适","planPriority":2}]}
                """);

        var proposal = Assert.Single(
            AiTaskCoordinator.ReadProposedChanges(message));

        Assert.Equal("change-1", proposal.ChangeId);
        Assert.Equal(PersonalAnimeChangeKind.UpsertPlan, proposal.Kind);
        Assert.Equal(2, proposal.PlanPriority);
    }

    [Fact]
    public async Task ConversationStore_ExportsAndImportsWithoutSecrets()
    {
        var paths = new AiPluginPaths(_root);
        var store = new ConversationStore(paths);
        var conversation = await store.CreateConversationAsync(
            AiTaskKind.CompareAnime,
            "测试会话",
            AiSettings.Default with { Model = "test-model" });
        await store.AddMessageAsync(new AiMessage(
            "message-1",
            conversation.ConversationId,
            "user",
            "比较这两部",
            DateTimeOffset.UtcNow,
            0,
            0,
            string.Empty));
        var path = Path.Combine(paths.ExportDirectory, "export.json");

        await store.ExportAsync(path);
        var json = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain("apiKey", json, StringComparison.OrdinalIgnoreCase);

        var secondRoot = Path.Combine(_root, "imported");
        var imported = new ConversationStore(new AiPluginPaths(secondRoot));
        await imported.ImportAsync(path);
        var restored = Assert.Single(await imported.GetConversationsAsync());
        Assert.Equal("测试会话", restored.Title);
        Assert.Single(await imported.GetMessagesAsync(restored.ConversationId));
    }

    [Fact]
    public async Task DpapiSecretStore_RoundTripsForCurrentUser()
    {
        var store = new DpapiSecretStore(new AiPluginPaths(_root));

        store.SaveApiKey(AiProviderKind.DeepSeek, "secret-for-test");

        Assert.Equal(
            "secret-for-test",
            store.LoadApiKey(AiProviderKind.DeepSeek));
        Assert.Null(store.LoadApiKey(AiProviderKind.Qwen));
        var raw = await File.ReadAllTextAsync(
            Path.Combine(_root, "secrets.dat"));
        Assert.DoesNotContain("secret-for-test", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAiAdapter_ParsesTextUsageAndToolStream()
    {
        const string sse = """
            data: {"type":"response.output_text.delta","delta":"建议"}

            data: {"type":"response.function_call_arguments.done","name":"propose_anime_changes","arguments":"{\"changes\":[]}"}

            data: {"type":"response.completed","response":{"usage":{"input_tokens":12,"output_tokens":7}}}

            data: [DONE]

            """;
        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
        }));
        var adapter = new OpenAiProviderAdapter(client);

        var result = await adapter.SendAsync(
            CreateProviderRequest(AiProviderKind.OpenAI),
            null,
            CancellationToken.None);

        Assert.Equal("建议", result.Text);
        Assert.Equal(12, result.InputTokens);
        Assert.Equal(7, result.OutputTokens);
        Assert.Empty(result.ProposedChanges);
    }

    [Fact]
    public async Task OpenAiAdapter_RecordsObservedWebSearchEvent()
    {
        const string sse = """
            data: {"type":"response.web_search_call.completed","item_id":"ws-1"}

            data: {"type":"response.output_text.delta","delta":"已检索"}

            data: [DONE]

            """;
        using var client = new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(
                    sse,
                    Encoding.UTF8,
                    "text/event-stream"),
            }));
        var adapter = new OpenAiProviderAdapter(client);

        var result = await adapter.SendAsync(
            CreateProviderRequest(AiProviderKind.OpenAI) with
            {
                AllowWebTools = true,
            },
            null,
            CancellationToken.None);

        Assert.Equal("网页搜索", result.ToolSummary);
    }

    [Fact]
    public async Task AnthropicAdapter_ContinuesPausedServerToolTurn()
    {
        var requestCount = 0;
        using var client = new HttpClient(new StubHandler(_ =>
        {
            requestCount++;
            var json = requestCount == 1
                ? """
                    {"stop_reason":"pause_turn","usage":{"input_tokens":3,"output_tokens":2},"content":[{"type":"server_tool_use","id":"srv-1","name":"web_search","input":{"query":"test"}},{"type":"web_search_tool_result","tool_use_id":"srv-1","content":[]}]}
                    """
                : """
                    {"stop_reason":"end_turn","usage":{"input_tokens":4,"output_tokens":5},"content":[{"type":"text","text":"检索完成"}]}
                    """;
            return new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }));
        var adapter = new AnthropicProviderAdapter(client);

        var result = await adapter.SendAsync(
            CreateProviderRequest(AiProviderKind.Anthropic) with
            {
                AllowWebTools = true,
            },
            null,
            CancellationToken.None);

        Assert.Equal(2, requestCount);
        Assert.Equal("检索完成", result.Text);
        Assert.Equal(7, result.InputTokens);
        Assert.Equal(7, result.OutputTokens);
        Assert.Equal("网页搜索", result.ToolSummary);
    }

    [Fact]
    public async Task GeminiAdapter_RecordsGroundingMetadata()
    {
        const string sse = """
            data: {"candidates":[{"groundingMetadata":{"webSearchQueries":["test"]},"content":{"parts":[{"text":"已检索"}]}}]}

            """;
        using var client = new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(
                    sse,
                    Encoding.UTF8,
                    "text/event-stream"),
            }));
        var adapter = new GeminiProviderAdapter(client);

        var result = await adapter.SendAsync(
            CreateProviderRequest(AiProviderKind.Gemini) with
            {
                AllowWebTools = true,
            },
            null,
            CancellationToken.None);

        Assert.Equal("Google Search", result.ToolSummary);
    }

    [Fact]
    public async Task ProviderAdapter_ClassifiesUnauthorizedWithoutResponseBody()
    {
        using var client = new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        var adapter = new OpenAiProviderAdapter(client);

        var exception = await Assert.ThrowsAsync<AiProviderException>(() =>
            adapter.SendAsync(
                CreateProviderRequest(AiProviderKind.OpenAI),
                null,
                CancellationToken.None));

        Assert.Equal(401, exception.StatusCode);
        Assert.DoesNotContain("test-key", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompatibleAdapter_RejectsUndeclaredWebToolsBeforeNetwork()
    {
        var called = false;
        using var client = new HttpClient(new StubHandler(_ =>
        {
            called = true;
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        var adapter = new OpenAiCompatibleProviderAdapter(client);
        var request = CreateProviderRequest(AiProviderKind.OpenAICompatible)
            with { AllowWebTools = true };

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            adapter.SendAsync(request, null, CancellationToken.None));
        Assert.False(called);
    }

    [Theory]
    [InlineData("https://api.deepseek.com/v1", "https://api.deepseek.com/v1/chat/completions")]
    [InlineData("https://dashscope.aliyuncs.com/compatible-mode/v1", "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions")]
    [InlineData("https://open.bigmodel.cn/api/paas/v4", "https://open.bigmodel.cn/api/paas/v4/chat/completions")]
    [InlineData("https://example.test", "https://example.test/v1/chat/completions")]
    public void CompatibleAdapter_PreservesVersionedBaseUrl(
        string baseUrl,
        string expected)
        => Assert.Equal(
            expected,
            OpenAiCompatibleProviderAdapter.BuildCompatibleUri(
                baseUrl,
                "chat/completions").AbsoluteUri);

    [Theory]
    [InlineData((int)AiProviderKind.DeepSeek)]
    [InlineData((int)AiProviderKind.Qwen)]
    [InlineData((int)AiProviderKind.Moonshot)]
    [InlineData((int)AiProviderKind.Zhipu)]
    [InlineData((int)AiProviderKind.Doubao)]
    public void DomesticProviderPresets_UseCompatibleProtocol(
        int providerValue)
    {
        var provider = (AiProviderKind)providerValue;
        var descriptor = AiProviderCatalog.Get(provider);

        Assert.True(descriptor.UsesOpenAiCompatibleApi);
        Assert.StartsWith("https://", descriptor.DocumentationUrl);
    }

    private static AiProviderRequest CreateProviderRequest(
        AiProviderKind provider)
        => new(
            "system",
            [],
            "user",
            new PersonalAnimeContextSnapshot(
                "snapshot",
                "test",
                PersonalAnimeDataCategory.PublicMetadata,
                DateTimeOffset.UtcNow,
                [],
                [],
                []),
            false,
            AiSettings.Default with
            {
                Provider = provider,
                Model = "test-model",
                TimeoutSeconds = 10,
            },
            "test-key");

    private sealed class StubHandler(
        Func<HttpRequestMessage, HttpResponseMessage> handler) :
        HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
