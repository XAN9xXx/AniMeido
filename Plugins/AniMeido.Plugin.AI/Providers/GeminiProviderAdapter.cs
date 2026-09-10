using AniMeido.Plugin.AI.Models;
using System.Text;
using System.Text.Json;

namespace AniMeido.Plugin.AI.Providers;

internal sealed class GeminiProviderAdapter(HttpClient httpClient) :
    ProviderAdapterBase(httpClient)
{
    public override AiProviderKind Kind => AiProviderKind.Gemini;

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
        var model = Uri.EscapeDataString(request.Settings.Model);
        var uri = BuildUri(
            request.Settings.BaseUrl,
            $"/v1beta/models/{model}:streamGenerateContent?alt=sse&key={Uri.EscapeDataString(request.ApiKey)}");
        using var message = new HttpRequestMessage(HttpMethod.Post, uri);
        var declarations = new List<object>
        {
            new
            {
                functionDeclarations = new[]
                {
                    new
                    {
                        name = "propose_anime_changes",
                        description = "仅提出 AniMeido 结构化变更，绝不直接执行。",
                        parameters = ProviderRequestBuilder.ChangeSchemaForGemini(),
                    },
                },
            },
        };
        if (request.AllowWebTools)
        {
            declarations.Add(new { googleSearch = new { } });
        }

        message.Content = JsonContent(new
        {
            systemInstruction = new
            {
                parts = new[] { new { text = request.SystemPrompt } },
            },
            contents = ProviderRequestBuilder.BuildGeminiContents(request),
            tools = declarations,
            generationConfig = new
            {
                maxOutputTokens = request.Settings.MaximumOutputTokens,
            },
        });
        using var response = await SendHttpAsync(
            message,
            effectiveToken);
        var text = new StringBuilder();
        var arguments = new List<string>();
        var inputTokens = 0;
        var outputTokens = 0;
        var usedWebSearch = false;
        await ReadSseAsync(response, item =>
        {
            if (item.TryGetProperty("usageMetadata", out var usage))
            {
                inputTokens = GetInt(usage, "promptTokenCount");
                outputTokens = GetInt(usage, "candidatesTokenCount");
            }

            if (!item.TryGetProperty("candidates", out var candidates)
                || candidates.GetArrayLength() == 0)
            {
                return true;
            }

            usedWebSearch |= candidates[0].TryGetProperty(
                "groundingMetadata",
                out _);
            if (!candidates[0].TryGetProperty("content", out var content)
                || !content.TryGetProperty("parts", out var parts))
            {
                return true;
            }

            foreach (var part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var textNode))
                {
                    var value = textNode.GetString() ?? string.Empty;
                    text.Append(value);
                    progress?.Report(value);
                }
                else if (part.TryGetProperty("functionCall", out var call)
                    && call.TryGetProperty("name", out var name)
                    && name.GetString() == "propose_anime_changes"
                    && call.TryGetProperty("args", out var args))
                {
                    arguments.Add(args.GetRawText());
                }
            }

            return true;
        }, effectiveToken);
        var parsed = ProviderResponseParser.Parse(text.ToString(), arguments);
        return new AiProviderResult(
            parsed.Text,
            parsed.Changes,
            inputTokens,
            outputTokens,
            BuildToolSummary(usedWebSearch, arguments.Count > 0));
    }

    public override async Task<IReadOnlyList<string>> GetModelsAsync(
        AiSettings settings,
        string apiKey,
        CancellationToken cancellationToken)
    {
        using var timeout = CreateRequestTimeout(settings, cancellationToken);
        using var message = new HttpRequestMessage(
            HttpMethod.Get,
            BuildUri(
                settings.BaseUrl,
                $"/v1beta/models?key={Uri.EscapeDataString(apiKey)}"));
        using var response = await SendHttpAsync(
            message,
            timeout.Token);
        var ids = await ReadModelIdsAsync(
            response,
            "models",
            "name",
            timeout.Token);
        return ids.Select(id => id.StartsWith("models/", StringComparison.Ordinal)
                ? id[7..]
                : id)
            .ToList();
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
            (true, true) => "Google Search；结构化变更提案",
            (true, false) => "Google Search",
            (false, true) => "结构化变更提案",
            _ => string.Empty,
        };
}
