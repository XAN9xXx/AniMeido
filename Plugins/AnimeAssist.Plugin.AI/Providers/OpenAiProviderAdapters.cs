using AniMeido.Plugin.AI.Models;
using System.Text;
using System.Text.Json;

namespace AniMeido.Plugin.AI.Providers;

internal sealed class OpenAiProviderAdapter(HttpClient httpClient) :
    ProviderAdapterBase(httpClient)
{
    public override AiProviderKind Kind => AiProviderKind.OpenAI;

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
        using var timeout = CreateRequestTimeout(
            request.Settings,
            cancellationToken);
        var effectiveToken = timeout.Token;
        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            BuildUri(request.Settings.BaseUrl, "/v1/responses"));
        SetBearer(message, request.ApiKey);
        var tools = new List<object>
        {
            new
            {
                type = "function",
                name = "propose_anime_changes",
                description = "仅提出 AniMeido 结构化变更，绝不直接执行。",
                strict = false,
                parameters = ProviderRequestBuilder.ChangeSchema(),
            },
        };
        if (request.AllowWebTools)
        {
            tools.Add(new { type = "web_search_preview" });
        }

        message.Content = JsonContent(new
        {
            model = request.Settings.Model,
            instructions = request.SystemPrompt,
            input = ProviderRequestBuilder.BuildUserText(request),
            max_output_tokens = request.Settings.MaximumOutputTokens,
            stream = true,
            tools,
        });

        using var response = await SendHttpAsync(
            message,
            effectiveToken);
        var text = new StringBuilder();
        var toolArguments = new List<string>();
        var usedWebSearch = false;
        var inputTokens = 0;
        var outputTokens = 0;
        await ReadSseAsync(response, item =>
        {
            var type = item.TryGetProperty("type", out var typeNode)
                ? typeNode.GetString()
                : null;
            usedWebSearch |= type?.StartsWith(
                "response.web_search_call.",
                StringComparison.Ordinal) == true;
            if (type == "response.output_text.delta"
                && item.TryGetProperty("delta", out var delta))
            {
                var value = delta.GetString() ?? string.Empty;
                text.Append(value);
                progress?.Report(value);
            }
            else if (type == "response.output_item.done"
                && item.TryGetProperty("item", out var outputItem)
                && outputItem.TryGetProperty("name", out var name)
                && name.GetString() == "propose_anime_changes"
                && outputItem.TryGetProperty("arguments", out var arguments))
            {
                toolArguments.Add(arguments.GetString() ?? string.Empty);
            }
            else if (type == "response.function_call_arguments.done"
                && item.TryGetProperty("name", out var functionName)
                && functionName.GetString() == "propose_anime_changes"
                && item.TryGetProperty("arguments", out var finalArguments))
            {
                toolArguments.Add(finalArguments.GetString() ?? string.Empty);
            }
            else if (type == "response.completed"
                && item.TryGetProperty("response", out var completed)
                && completed.TryGetProperty("usage", out var usage))
            {
                inputTokens = GetInt(usage, "input_tokens");
                outputTokens = GetInt(usage, "output_tokens");
            }

            return true;
        }, effectiveToken);
        var parsed = ProviderResponseParser.Parse(text.ToString(), toolArguments);
        return new AiProviderResult(
            parsed.Text,
            parsed.Changes,
            inputTokens,
            outputTokens,
            BuildToolSummary(usedWebSearch, toolArguments.Count > 0));
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
        SetBearer(message, apiKey);
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

internal sealed class OpenAiCompatibleProviderAdapter(HttpClient httpClient) :
    ProviderAdapterBase(httpClient)
{
    public override AiProviderKind Kind => AiProviderKind.OpenAICompatible;

    public override AiProviderCapabilities Capabilities => new(
        true,
        true,
        false,
        true);

    public override async Task<AiProviderResult> SendAsync(
        AiProviderRequest request,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        using var timeout = CreateRequestTimeout(
            request.Settings,
            cancellationToken);
        var effectiveToken = timeout.Token;
        if (request.AllowWebTools)
        {
            throw new NotSupportedException(
                "自定义 OpenAI-compatible 端点未声明网页搜索能力。请关闭联网工具。");
        }

        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            BuildCompatibleUri(request.Settings.BaseUrl, "chat/completions"));
        SetBearer(message, request.ApiKey);
        message.Content = JsonContent(new
        {
            model = request.Settings.Model,
            stream = true,
            max_tokens = request.Settings.MaximumOutputTokens,
            messages = new object[]
            {
                new { role = "system", content = request.SystemPrompt },
                new { role = "user", content = ProviderRequestBuilder.BuildUserText(request) },
            },
            tools = new[]
            {
                new
                {
                    type = "function",
                    function = new
                    {
                        name = "propose_anime_changes",
                        description = "仅提出 AniMeido 结构化变更，绝不直接执行。",
                        parameters = ProviderRequestBuilder.ChangeSchema(),
                    },
                },
            },
        });
        using var response = await SendHttpAsync(
            message,
            effectiveToken);
        var text = new StringBuilder();
        var arguments = new Dictionary<int, StringBuilder>();
        await ReadSseAsync(response, item =>
        {
            if (!item.TryGetProperty("choices", out var choices)
                || choices.GetArrayLength() == 0
                || !choices[0].TryGetProperty("delta", out var delta))
            {
                return true;
            }

            if (delta.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.String)
            {
                var value = content.GetString() ?? string.Empty;
                text.Append(value);
                progress?.Report(value);
            }

            if (delta.TryGetProperty("tool_calls", out var calls))
            {
                foreach (var call in calls.EnumerateArray())
                {
                    var index = call.TryGetProperty("index", out var indexNode)
                        ? indexNode.GetInt32()
                        : 0;
                    if (!arguments.TryGetValue(index, out var builder))
                    {
                        builder = new StringBuilder();
                        arguments[index] = builder;
                    }

                    if (call.TryGetProperty("function", out var function)
                        && function.TryGetProperty("arguments", out var part))
                    {
                        builder.Append(part.GetString());
                    }
                }
            }

            return true;
        }, effectiveToken);
        var parsed = ProviderResponseParser.Parse(
            text.ToString(),
            arguments.Values.Select(value => value.ToString()));
        return new AiProviderResult(
            parsed.Text,
            parsed.Changes,
            0,
            0,
            arguments.Count > 0 ? "结构化变更提案" : string.Empty);
    }

    public override async Task<IReadOnlyList<string>> GetModelsAsync(
        AiSettings settings,
        string apiKey,
        CancellationToken cancellationToken)
    {
        using var timeout = CreateRequestTimeout(settings, cancellationToken);
        using var message = new HttpRequestMessage(
            HttpMethod.Get,
            BuildCompatibleUri(settings.BaseUrl, "models"));
        SetBearer(message, apiKey);
        using var response = await SendHttpAsync(
            message,
            timeout.Token);
        return await ReadModelIdsAsync(response, "data", "id", timeout.Token);
    }

    internal static Uri BuildCompatibleUri(string baseUrl, string endpoint)
    {
        var trimmed = baseUrl.TrimEnd('/');
        var lastSegment = new Uri(trimmed).AbsolutePath
            .TrimEnd('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault();
        var includesVersion = lastSegment is { Length: > 1 }
            && (lastSegment[0] is 'v' or 'V')
            && lastSegment[1..].All(char.IsDigit);
        return BuildUri(
            trimmed,
            includesVersion ? endpoint : $"v1/{endpoint}");
    }
}
