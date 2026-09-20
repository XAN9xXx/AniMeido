using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AniMeido.Plugin.Base.Services
{
    /// <summary>Archive 数据是否已经过旧，决定先访问 Archive 还是在线代理。</summary>
    internal interface IArchiveFreshness
    {
        /// <summary>Archive 数据过旧：优先走在线代理，Archive 退为备用。</summary>
        bool PreferFallback { get; }

        /// <summary>
        /// 需要时检查一次。本次运行第一次检查会短暂等待；之后到期的检查在后台进行，不拖慢请求。
        /// </summary>
        Task EnsureCheckedAsync(CancellationToken ct);
    }

    /// <summary>
    /// 通过 Archive 的 <c>/healthz</c> 判断数据新鲜度：上游每周更新一次，
    /// 数据包超过 <see cref="StaleAfter"/> 没更新就认为过旧，此时先走在线代理。
    /// 检查失败不改变判断，由原有的降级处理连不上的情况。
    /// </summary>
    internal sealed class ArchiveFreshnessMonitor : IArchiveFreshness
    {
        internal static readonly TimeSpan StaleAfter = TimeSpan.FromDays(10);
        internal static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
        // 检查失败时早一点再试，不必等满一个间隔。
        internal static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(30);
        // 本次运行的第一次请求最多等这么久，超时就按原顺序继续。
        internal static readonly TimeSpan FirstCheckWait = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

        private readonly IHttpClientFactory _httpFactory;
        private readonly ILogger _logger;
        private readonly TimeProvider _time;
        private readonly object _gate = new();
        private DateTimeOffset _nextCheck = DateTimeOffset.MinValue;
        private Task? _running;
        private bool _checkedOnce;

        public ArchiveFreshnessMonitor(
            IHttpClientFactory httpFactory,
            ILogger logger,
            TimeProvider? timeProvider = null)
        {
            _httpFactory = httpFactory;
            _logger = logger;
            _time = timeProvider ?? TimeProvider.System;
        }

        public bool PreferFallback { get; private set; }

        /// <summary>正在进行的检查，测试用来等待它完成。</summary>
        internal Task PendingCheck
        {
            get
            {
                lock (_gate)
                {
                    return _running ?? Task.CompletedTask;
                }
            }
        }

        public async Task EnsureCheckedAsync(CancellationToken ct)
        {
            Task check;
            lock (_gate)
            {
                if (_running is { IsCompleted: false } running)
                {
                    check = running;
                }
                else if (_time.GetUtcNow() >= _nextCheck)
                {
                    _running = check = CheckAsync();
                }
                else
                {
                    return;
                }

                // 已经检查过一次之后，到期的检查在后台进行。
                if (_checkedOnce)
                    return;
            }

            try
            {
                await check.WaitAsync(FirstCheckWait, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // 第一次检查太慢：先按当前顺序发请求，结果稍后生效。
            }
        }

        private async Task CheckAsync()
        {
            var stale = PreferFallback;
            var checkedSuccessfully = false;
            TimeSpan age = default;
            try
            {
                using var timeout = new CancellationTokenSource(ProbeTimeout);
                var client = _httpFactory.CreateClient(BangumiApiClient.ArchiveClientName);
                using var response = await client
                    .GetAsync("/healthz", timeout.Token)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                var health = JsonSerializer.Deserialize<ArchiveHealth>(json, BangumiApiClient.JsonOptions);
                if (health?.Archive?.ArchiveCreatedAt is { } createdAt
                    && DateTimeOffset.TryParse(
                        createdAt,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal,
                        out var created))
                {
                    age = _time.GetUtcNow() - created;
                    stale = age > StaleAfter;
                    checkedSuccessfully = true;
                }
            }
            catch (Exception ex) when (
                ex is HttpRequestException
                or OperationCanceledException
                or JsonException
                or IOException)
            {
                _logger.LogDebug(ex, "Bangumi Archive health check failed; keeping the current request order");
            }

            lock (_gate)
            {
                _checkedOnce = true;
                _nextCheck = _time.GetUtcNow() + (checkedSuccessfully ? CheckInterval : RetryInterval);
                if (stale == PreferFallback)
                    return;

                PreferFallback = stale;
            }

            if (stale)
            {
                _logger.LogInformation(
                    "Bangumi Archive data is {Days} days old; preferring the online API",
                    (int)age.TotalDays);
            }
            else
            {
                _logger.LogInformation("Bangumi Archive data is current again; preferring the Archive");
            }
        }

        private sealed record ArchiveHealth(ArchiveHealthMetadata? Archive);

        private sealed record ArchiveHealthMetadata(string? ArchiveCreatedAt);
    }
}
