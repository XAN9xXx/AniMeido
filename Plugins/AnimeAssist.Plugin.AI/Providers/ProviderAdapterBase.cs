using AniMeido.Plugin.AI.Models;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AniMeido.Plugin.AI.Providers;

internal abstract class ProviderAdapterBase(HttpClient httpClient) :
    IAiProviderAdapter
{
    protected HttpClient HttpClient { get; } = httpClient;

    public abstract AiProviderKind Kind { get; }

    public abstract AiProviderCapabilities Capabilities { get; }

    public abstract Task<AiProviderResult> SendAsync(
        AiProviderRequest request,
        IProgress<string>? progress,
        CancellationToken cancellationToken);

    public abstract Task<IReadOnlyList<string>> GetModelsAsync(
        AiSettings settings,
        string apiKey,
        CancellationToken cancellationToken);

    protected static StringContent JsonContent(object value)
        => new(
            JsonSerializer.Serialize(value),
            Encoding.UTF8,
            "application/json");

    protected static Uri BuildUri(string baseUrl, string path)
        => new($"{baseUrl.TrimEnd('/')}/{path.TrimStart('/')}");

    protected static void SetBearer(HttpRequestMessage message, string apiKey)
        => message.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            apiKey);

    protected async Task<HttpResponseMessage> SendHttpAsync(
        HttpRequestMessage message,
        CancellationToken cancellationToken)
    {
        var response = await HttpClient.SendAsync(
            message,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            response.Dispose();
            throw new AiProviderException(
                status,
                status switch
                {
                    401 or 403 => "Provider 拒绝了凭据或权限。",
                    429 => "Provider 当前限流，请稍后重试。",
                    _ => $"Provider 请求失败（HTTP {status}）。",
                });
        }

        return response;
    }

    protected static CancellationTokenSource CreateRequestTimeout(
        AiSettings settings,
        CancellationToken cancellationToken)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        return timeout;
    }

    protected static async Task ReadSseAsync(
        HttpResponseMessage response,
        Func<JsonElement, bool> onEvent,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(
            cancellationToken);
        using var reader = new StreamReader(stream);
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                return;
            }

            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var data = line[5..].Trim();
            if (data.Length == 0 || string.Equals(data, "[DONE]", StringComparison.Ordinal))
            {
                continue;
            }

            using var document = JsonDocument.Parse(data);
            if (!onEvent(document.RootElement))
            {
                return;
            }
        }
    }

    protected static async Task<IReadOnlyList<string>> ReadModelIdsAsync(
        HttpResponseMessage response,
        string arrayName,
        string idName,
        CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken));
        if (!document.RootElement.TryGetProperty(arrayName, out var models)
            || models.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return models.EnumerateArray()
            .Select(item => item.TryGetProperty(idName, out var id)
                ? id.GetString()
                : null)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

internal sealed class AiProviderException(int statusCode, string message) :
    Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
