using AnimeAssist.Plugin.Base.Exceptions;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AnimeAssist.Plugin.Base.Services
{
    /// <summary>
    /// 用于访问Bangumi API的客户端类，负责发送HTTP请求、处理响应并解析JSON数据。
    /// </summary>
    internal class BangumiApiClient
    {
        private readonly IHttpClientFactory _httpFactory;
        private readonly ILogger<BangumiApiClient> _logger;
        
        /// <summary>
        /// 转换Json数据的选项配置。
        /// </summary>
        /// <remarks>
        /// 设置了属性名称不区分大小写和使用蛇形命名转换，以适应Bangumi API的字段命名习惯。
        /// </remarks>
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        };

        internal BangumiApiClient(IHttpClientFactory httpFactory, ILogger<BangumiApiClient> logger)
        {
            _httpFactory = httpFactory;
            _logger = logger;
        }

        /// <summary>
        /// 用于获取并解析JSON数据。
        /// </summary>
        /// <typeparam name="T">返回泛型。</typeparam>
        /// <param name="url">请求的URL。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>解析后的对象。</returns>
        /// <exception cref="BangumiApiException">当请求或解析失败时抛出。</exception>
        internal async Task<T?> GetJsonAsync<T>(string url, CancellationToken ct)
        {
            var client = _httpFactory.CreateClient("BangumiAPI");
            string? json;

            // 尝试获取JSON数据
            try
            {
                json = await client.GetStringAsync(url, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "Error fetching data from Bangumi API");
                throw new BangumiApiException("Error fetching data from Bangumi API", ex);
            }
            // 区分请求被取消和请求超时的情况
            catch (TaskCanceledException ex) when (ex.CancellationToken == ct)
            {
                _logger.LogWarning("Request to Bangumi API was canceled");
                throw;
            }
            catch (TaskCanceledException ex)
            {
                _logger.LogError(ex, "Request to Bangumi API timed out");
                throw new BangumiApiException("Request to Bangumi API timed out", ex);
            }

            // 尝试解析JSON数据
            try
            {
                return JsonSerializer.Deserialize<T>(json, JsonOptions);
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Error parsing JSON from Bangumi API");
                throw new BangumiApiException("Error parsing JSON from Bangumi API", ex);
            }
        }
    }
}
