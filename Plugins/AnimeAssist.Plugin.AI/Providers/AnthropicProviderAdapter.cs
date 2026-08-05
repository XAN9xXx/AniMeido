using AniMeido.Plugin.AI.Models;
using System.Text;
using System.Text.Json;

namespace AniMeido.Plugin.AI.Providers;

internal sealed class AnthropicProviderAdapter(HttpClient httpClient) :
    ProviderAdapterBase(httpClient)
{
    public override AiProviderKind Kind => AiProviderKind.Anthropic;

    public override AiProviderCapabilities Capabilities => new(
        true,
        true,
        true,
        true);

    public override async Task<AiProviderResult> SendAsync(
        AiProviderRequest request,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (request.AllowWebTools)
        {
            return await SendWithWebSearchAsync(
                request,
                progress,
                cancellationToken);
        }

        using var timeout = CreateRequestTimeout(
            request.Settings,
            cancellationToken);
        var effectiveToken = timeout.Token;
        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            BuildUri(request.Settings.BaseUrl, "/v1/messages"));
        message.Headers.Add("x-api-key", request.ApiKey);
        message.Headers.Add("anthropic-version", "2023-06-01");
        var tools = new List<object> { ProviderRequestBuilder.ChangeTool() };
        message.Content = JsonContent(new
        {
            model = request.Settings.Model,
            system = request.SystemPrompt,
            max_tokens = request.Settings.MaximumOutputTokens,
            stream = true,
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = ProviderRequestBuilder.BuildUserText(request),
                },
            },
            tools,
        });
        using var response = await SendHttpAsync(
            message,
            effectiveToken);
        var text = new StringBuilder();
        var toolJson = new Dictionary<int, StringBuilder>();
        var toolNames = new Dictionary<int, string>();
        var inputTokens = 0;
        var outputTokens = 0;
        await ReadSseAsync(response, item =>
        {
            var type = item.TryGetProperty("type", out var typeNode)
                ? typeNode.GetString()
                : null;
            if (type == "content_block_start"
                && item.TryGetProperty("index", out var indexNode)
                && item.TryGetProperty("content_block", out var block)
                && block.TryGetProperty("name", out var nameNode))
            {
                toolNames[indexNode.GetInt32()] = nameNode.GetString() ?? string.Empty;
            }
            else if (type == "content_block_delta"
                && item.TryGetProperty("index", out indexNode)
                && item.TryGetProperty("delta", out var delta))
            {
                var index = indexNode.GetInt32();
                var deltaType = delta.TryGetProperty("type", out var deltaTypeNode)
                    ? deltaTypeNode.GetString()
                    : null;
                if (deltaType == "text_delta"
                    && delta.TryGetProperty("text", out var valueNode))
                {
                    var value = valueNode.GetString() ?? string.Empty;
                    text.Append(value);
                    progress?.Report(value);
                }
                else if (deltaType == "input_json_delta"
                    && delta.TryGetProperty("partial_json", out var jsonNode))
                {
                    if (!toolJson.TryGetValue(index, out var builder))
                    {
                        builder = new StringBuilder();
                        toolJson[index] = builder;
                    }

                    builder.Append(jsonNode.GetString());
                }
            }
            else if (type == "message_start"
                && item.TryGetProperty("message", out var started)
                && started.TryGetProperty("usage", out var startUsage))
            {
                inputTokens = GetInt(startUsage, "input_tokens");
            }
            else if (type == "message_delta"
                && item.TryGetProperty("usage", out var endUsage))
            {
                outputTokens = GetInt(endUsage, "output_tokens");
            }

            return true;
        }, effectiveToken);
        var proposalArguments = toolJson
            .Where(pair => toolNames.TryGetValue(pair.Key, out var name)
                && name == "propose_anime_changes")
            .Select(pair => pair.Value.ToString());
        var parsed = ProviderResponseParser.Parse(text.ToString(), proposalArguments);
        return new AiProviderResult(
            parsed.Text,
            parsed.Changes,
            inputTokens,
            outputTokens,
            toolJson.Count > 0 ? "结构化变更提案" : string.Empty);
    }

    private async Task<AiProviderResult> SendWithWebSearchAsync(
        AiProviderRequest request,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        using var timeout = CreateRequestTimeout(
            request.Settings,
            cancellationToken);
        var messages = new List<object>
        {
            new
            {
                role = "user",
                content = ProviderRequestBuilder.BuildUserText(request),
            },
        };
        var tools = new object[]
        {
            ProviderRequestBuilder.ChangeTool(),
            new
            {
                type = "web_search_20250305",
                name = "web_search",
                max_uses = 5,
            },
        };
        var text = new StringBuilder();
        var proposalArguments = new List<string>();
        var inputTokens = 0;
        var outputTokens = 0;
        var usedWebSearch = false;

        for (var continuation = 0; continuation < 4; continuation++)
        {
            using var message = new HttpRequestMessage(
                HttpMethod.Post,
                BuildUri(request.Settings.BaseUrl, "/v1/messages"));
            message.Headers.Add("x-api-key", request.ApiKey);
            message.Headers.Add("anthropic-version", "2023-06-01");
            message.Content = JsonContent(new
            {
                model = request.Settings.Model,
                system = request.SystemPrompt,
                max_tokens = request.Settings.MaximumOutputTokens,
                stream = false,
                messages,
                tools,
            });
            using var response = await SendHttpAsync(message, timeout.Token);
            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(timeout.Token));
            var root = document.RootElement;
            if (root.TryGetProperty("usage", out var usage))
            {
                inputTokens += GetInt(usage, "input_tokens");
                outputTokens += GetInt(usage, "output_tokens");
            }

            if (!root.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException(
                    "Anthropic 响应缺少 content 数组。");
            }

            foreach (var block in content.EnumerateArray())
            {
                var blockType = block.TryGetProperty("type", out var typeNode)
                    ? typeNode.GetString()
                    : null;
                usedWebSearch |= blockType is "server_tool_use"
                    or "web_search_tool_result";
                if (blockType == "text"
                    && block.TryGetProperty("text", out var textNode))
                {
                    var value = textNode.GetString() ?? string.Empty;
                    text.Append(value);
                    progress?.Report(value);
                }
                else if (blockType == "tool_use"
                    && block.TryGetProperty("name", out var nameNode)
                    && nameNode.GetString() == "propose_anime_changes"
                    && block.TryGetProperty("input", out var inputNode))
                {
                    proposalArguments.Add(inputNode.GetRawText());
                }
            }

            var stopReason = root.TryGetProperty("stop_reason", out var stopNode)
                ? stopNode.GetString()
                : null;
            if (stopReason != "pause_turn")
            {
                var parsed = ProviderResponseParser.Parse(
                    text.ToString(),
                    proposalArguments);
                return new AiProviderResult(
                    parsed.Text,
                    parsed.Changes,
                    inputTokens,
                    outputTokens,
                    BuildToolSummary(
                        usedWebSearch,
                        proposalArguments.Count > 0));
            }

            messages.Add(new
            {
                role = "assistant",
                content = content.Clone(),
            });
        }

        throw new InvalidOperationException(
            "Anthropic 网页搜索连续暂停，未能在限定轮次内完成。");
    }

    public override async Task<IReadOnlyList<string>> GetModelsAsync(
        AiSettings settings,
        string apiKey,
        CancellationToken cancellationToken)
    {
        using var timeout = CreateRequestTimeout(settings, cancellationToken);
        using var message = new HttpRequestMessage(
            HttpMethod.Get,
            BuildUri(settings.BaseUrl, "/v1/models"));
        message.Headers.Add("x-api-key", apiKey);
        message.Headers.Add("anthropic-version", "2023-06-01");
        using var response = await SendHttpAsync(
            message,
            timeout.Token);
        return await ReadModelIdsAsync(response, "data", "id", timeout.Token);
    }

    private static int GetInt(JsonElement element, string name)
        => element.TryGetProperty(name, out var value)
            && value.TryGetInt32(out var result)
                ? result
                : 0;

    private static string BuildToolSummary(
        bool usedWebSearch,
        bool proposedChanges)
        => (usedWebSearch, proposedChanges) switch
        {
            (true, true) => "网页搜索；结构化变更提案",
            (true, false) => "网页搜索",
            (false, true) => "结构化变更提案",
            _ => string.Empty,
        };
}
