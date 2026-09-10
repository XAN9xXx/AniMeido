using AniMeido.Plugin.Base.Exceptions;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;

namespace AniMeido.Plugin.Base.Services
{
    /// <summary>
    /// 按顺序访问本地 Archive 与在线 Bangumi API，并解析 JSON 响应。
    /// </summary>
    public class BangumiApiClient
    {
        internal const string ArchiveClientName = "BangumiArchiveAPI";
        internal const string FallbackClientName = "BangumiAPI";

        private static readonly string[] ClientNames =
        [
            ArchiveClientName,
            FallbackClientName,
        ];

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        };

        private readonly IHttpClientFactory _httpFactory;
        private readonly ILogger<BangumiApiClient> _logger;

        public BangumiApiClient(
            IHttpClientFactory httpFactory,
            ILogger<BangumiApiClient> logger)
        {
            _httpFactory = httpFactory;
            _logger = logger;
        }

        /// <summary>
        /// 获取并解析 JSON。Archive 请求异常或返回无效响应时自动访问在线 API。
        /// </summary>
        internal Task<T?> GetJsonAsync<T>(string url, CancellationToken ct)
        {
            return SendJsonAsync<T>(
                url,
                static () => new HttpRequestMessage { Method = HttpMethod.Get },
                ct);
        }

        /// <summary>
        /// 发送 POST 请求并解析 JSON。每次尝试都创建独立请求正文，以支持可靠降级。
        /// </summary>
        internal Task<T?> PostJsonAsync<T>(string url, object body, CancellationToken ct)
        {
            var jsonBody = JsonSerializer.Serialize(body, JsonOptions);
            return SendJsonAsync<T>(
                url,
                () => new HttpRequestMessage
                {
                    Method = HttpMethod.Post,
                    Content = new StringContent(jsonBody, Encoding.UTF8, "application/json"),
                },
                ct);
        }

        private async Task<T?> SendJsonAsync<T>(
            string url,
            Func<HttpRequestMessage> createRequest,
            CancellationToken ct)
        {
            Exception? lastFailure = null;

            for (var index = 0; index < ClientNames.Length; index++)
            {
                var clientName = ClientNames[index];
                var isFallback = index == ClientNames.Length - 1;
                var client = _httpFactory.CreateClient(clientName);

                try
                {
                    using var request = createRequest();
                    request.RequestUri = new Uri(url, UriKind.RelativeOrAbsolute);
                    using var response = await client.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        ct).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();

                    var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    return JsonSerializer.Deserialize<T>(json, JsonOptions);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    _logger.LogInformation("Bangumi request was canceled by the caller");
                    throw;
                }
                catch (Exception ex) when (
                    ex is HttpRequestException or OperationCanceledException or JsonException or IOException)
                {
                    lastFailure = ex;
                    if (!isFallback)
                    {
                        _logger.LogWarning(
                            ex,
                            "Bangumi Archive request failed for {Url}; falling back to the online API",
                            url);
                        continue;
                    }
                }

                break;
            }

            _logger.LogError(
                lastFailure,
                "Bangumi Archive and online API requests both failed for {Url}",
                url);
            throw new BangumiApiException(
                "Bangumi Archive and online API requests both failed",
                lastFailure ?? new InvalidOperationException("No Bangumi data source was attempted"));
        }
    }
}
