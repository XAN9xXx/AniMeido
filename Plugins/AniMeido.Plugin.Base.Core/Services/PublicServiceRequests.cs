using System.Net;
using AniMeido.Plugin.Base.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AniMeido.Plugin.Base.Services;

/// <summary>一个公共服务地址的发送资格、冷却和 HTTP 读取。</summary>
public sealed class PublicServiceRequests
{
    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly TimeSpan _attemptTimeout;
    private readonly SemaphoreSlim _sending;
    private readonly object _cooldownSync = new();
    private DateTimeOffset _notBefore;

    internal PublicServiceRequests(HttpClient http, TimeSpan? attemptTimeout = null,
        TimeProvider? time = null, ILogger? logger = null, int maxConcurrency = 4)
    {
        _http = http;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
        _attemptTimeout = attemptTimeout ?? TimeSpan.FromSeconds(10);
        _sending = new SemaphoreSlim(maxConcurrency, maxConcurrency);
    }

    internal async Task<T> ReadAsync<T>(string path,
        Func<HttpResponseMessage, CancellationToken, Task<T>> read,
        CancellationToken ct, ServiceReadBudget? budget = null, HttpMethod? method = null)
    {
        budget ??= new();
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await ReadOnceAsync(path, read, ct, budget, method).ConfigureAwait(false);
            }
            catch (MetadataServiceException ex) when (ex.IsRateLimited && attempt == 0)
            {
                // 下一次发送重新取得资格；长冷却会由同一预算立即结束本次读取。
            }
        }
    }

    internal async Task<T> ReadOnceAsync<T>(string path,
        Func<HttpResponseMessage, CancellationToken, Task<T>> read,
        CancellationToken ct, ServiceReadBudget budget, HttpMethod? method = null)
    {
        await AcquireAsync(budget, ct).ConfigureAwait(false);
        try
        {
            using var timeout = new CancellationTokenSource(_attemptTimeout, _time);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            var token = cancellation.Token;
            using var request = new HttpRequestMessage(method ?? HttpMethod.Get, path);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                ObserveRateLimit(response);
                throw MetadataServiceException.RateLimited();
            }
            var result = await read(response, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            _sending.Release();
        }
    }

    internal void ObserveRateLimit(HttpResponseMessage response)
    {
        var now = _time.GetUtcNow();
        var retry = response.Headers.RetryAfter;
        var delay = retry?.Delta ?? (retry?.Date is { } date ? date - now : TimeSpan.FromSeconds(2));
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
        // 极大的合法秒数也保留为冷却，避免日期加法溢出。
        var until = delay > DateTimeOffset.MaxValue - now ? DateTimeOffset.MaxValue : now + delay;
        bool report;
        lock (_cooldownSync)
        {
            report = now >= _notBefore;
            if (until > _notBefore) _notBefore = until;
        }
        if (report)
            _logger.LogWarning("Public service {Service} rate limited requests; cooling down until {Until}.",
                _http.BaseAddress?.Authority ?? response.RequestMessage?.RequestUri?.Authority, until);
    }

    private TimeSpan CooldownRemaining()
    {
        lock (_cooldownSync)
        {
            var remaining = _notBefore - _time.GetUtcNow();
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    private async Task AcquireAsync(ServiceReadBudget budget, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var delay = CooldownRemaining();
            if (delay > TimeSpan.Zero)
            {
                if (delay > budget.Remaining) throw MetadataServiceException.RateLimited();
                var started = _time.GetTimestamp();
                try { await Task.Delay(delay, _time, ct).ConfigureAwait(false); }
                finally { budget.Spend(_time.GetElapsedTime(started)); }
                continue;
            }

            await _sending.WaitAsync(ct).ConfigureAwait(false);
            if (CooldownRemaining() == TimeSpan.Zero) return;
            _sending.Release();
        }
    }
}

internal sealed class ServiceReadBudget
{
    internal TimeSpan Remaining { get; private set; } = TimeSpan.FromSeconds(30);
    internal void Spend(TimeSpan elapsed) => Remaining = TimeSpan.FromTicks(Math.Max(0, (Remaining - elapsed).Ticks));
}
