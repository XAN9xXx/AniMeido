using AniMeido.Plugin.Base.Exceptions;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;

namespace AniMeido.Plugin.Base.Services
{
    /// <summary>本次运行中各数据来源的结果计数，用于排查。</summary>
    /// <param name="Archive">由本地 Archive 返回。</param>
    /// <param name="Fallback">由在线代理返回。</param>
    /// <param name="Failed">两个来源都失败。</param>
    public sealed record BangumiRouteCounts(long Archive, long Fallback, long Failed);

    /// <summary>
    /// 按顺序访问本地 Archive 与在线 Bangumi API，并解析 JSON 响应。
    /// Archive 数据过旧时顺序反过来：先在线 API，Archive 退为备用。
    /// </summary>
    public class BangumiApiClient
    {
        internal const string ArchiveClientName = "BangumiArchiveAPI";
        internal const string FallbackClientName = "BangumiAPI";

        private static readonly string[] ArchiveFirst =
        [
            ArchiveClientName,
            FallbackClientName,
        ];

        // Archive 数据过旧时的顺序：在线 API 的数据更新，Archive 仍作为备用，避免代理故障时无数据。
        private static readonly string[] FallbackFirst =
        [
            FallbackClientName,
            ArchiveClientName,
        ];

        internal static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        };

        private readonly IHttpClientFactory _httpFactory;
        private readonly ILogger<BangumiApiClient> _logger;
        private readonly IArchiveFreshness _freshness;
        private long _archiveCount;
        private long _fallbackCount;
        private long _failedCount;
        // 最近一次成功的来源，换了来源时记一条日志，不必每条请求都记。
        private string _lastServedBy = "";

        public BangumiApiClient(
            IHttpClientFactory httpFactory,
            ILogger<BangumiApiClient> logger)
            : this(httpFactory, logger, new ArchiveFreshnessMonitor(httpFactory, logger))
        {
        }

        internal BangumiApiClient(
            IHttpClientFactory httpFactory,
            ILogger<BangumiApiClient> logger,
            IArchiveFreshness freshness)
        {
            _httpFactory = httpFactory;
            _logger = logger;
            _freshness = freshness;
        }

        /// <summary>本次运行中由各来源返回的次数，排查时用。</summary>
        public BangumiRouteCounts RouteCounts => new(
            Interlocked.Read(ref _archiveCount),
            Interlocked.Read(ref _fallbackCount),
            Interlocked.Read(ref _failedCount));

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

        /// <summary>记下这次由哪个来源返回；来源发生变化时记一条日志。</summary>
        private void RecordServed(string clientName, string url)
        {
            if (clientName == ArchiveClientName)
                Interlocked.Increment(ref _archiveCount);
            else
                Interlocked.Increment(ref _fallbackCount);

            _logger.LogDebug("Bangumi request to {Client} succeeded for {Url}", clientName, url);
            if (Interlocked.Exchange(ref _lastServedBy, clientName) == clientName)
                return;

            _logger.LogInformation("Bangumi requests are now served by {Client}", clientName);
        }

        private async Task<T?> SendJsonAsync<T>(
            string url,
            Func<HttpRequestMessage> createRequest,
            CancellationToken ct)
        {
            Exception? lastFailure = null;
            await _freshness.EnsureCheckedAsync(ct).ConfigureAwait(false);
            var clientNames = _freshness.PreferFallback ? FallbackFirst : ArchiveFirst;

            for (var index = 0; index < clientNames.Length; index++)
            {
                var clientName = clientNames[index];
                var isLast = index == clientNames.Length - 1;
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
                    var value = JsonSerializer.Deserialize<T>(json, JsonOptions);
                    RecordServed(clientName, url);
                    return value;
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
                    if (!isLast)
                    {
                        _logger.LogWarning(
                            ex,
                            "Bangumi request to {Client} failed for {Url}; trying the next source",
                            clientName,
                            url);
                        continue;
                    }
                }

                break;
            }

            Interlocked.Increment(ref _failedCount);
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
