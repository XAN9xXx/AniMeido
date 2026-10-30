using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Exceptions;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json;

namespace AniMeido.Plugin.Base.Services;

public sealed partial class MetadataDataSource
{
    internal const string VersionCacheKey = "metadata:data-version";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _versionGate = new(1, 1);
    private readonly Dictionary<string, CacheMissGate> _cacheMissGates = [];
    private readonly object _cacheMissGateLock = new();
    private string? _dataVersion;
    private DateTimeOffset _nextVersionCheck;
    internal event Action<string>? DataVersionChanged;
    internal event Action<int>? MergedDetailObserved;

    internal void QueueMergedCheck(int id) => MergedDetailObserved?.Invoke(id);

    internal async Task<string> GetCurrentDataVersionAsync(CancellationToken ct)
    {
        await EnsureVersionAsync(ct).ConfigureAwait(false);
        return _dataVersion!;
    }

    internal static string GetSeasonCacheKey(int year, Season season, string dataVersion) =>
        VersionedKey($"metadata:season:{year}:{season}", dataVersion);

    internal static async Task<string> GetSeasonCacheKeyAsync(CacheService cache, int year, Season season) =>
        GetSeasonCacheKey(year, season, await cache.GetCacheAllowExpiredAsync(VersionCacheKey) ?? "unknown");

    private static string VersionedKey(string key, string version) => $"{key}:data:{version}";

    // HEAD 只核对响应头，最多每五分钟一次；持久化版本供重启后离线定位同一组缓存。
    private async Task EnsureVersionAsync(CancellationToken ct)
    {
        await _versionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_dataVersion is not null && _time.GetUtcNow() < _nextVersionCheck) return;
            _dataVersion ??= await _cacheService.GetCacheAllowExpiredAsync(VersionCacheKey).ConfigureAwait(false);
            try
            {
                var generation = _cacheService.CaptureGeneration();
                using var request = new HttpRequestMessage(HttpMethod.Head, "/v1/works?limit=1");
                using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await ObserveVersionAsync(response, generation).ConfigureAwait(false);
                _nextVersionCheck = _time.GetUtcNow().AddMinutes(5);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && IsServiceFailure(ex))
            {
                if (_dataVersion is null)
                    throw new MetadataServiceException("无法取得资料服务数据版本。", ex);
                _nextVersionCheck = _time.GetUtcNow().AddMinutes(1);
                _logger.LogWarning(ex, "Version check failed; using cached metadata version {Version}", _dataVersion);
            }
        }
        finally { _versionGate.Release(); }
    }

    private async Task<string> ObserveVersionAsync(HttpResponseMessage response, long generation)
    {
        var version = response.Headers.TryGetValues("X-Data-Version", out var values)
            ? values.FirstOrDefault() : null;
        if (string.IsNullOrWhiteSpace(version))
            throw new MetadataServiceException("资料服务未返回 X-Data-Version。");
        var changed = Interlocked.Exchange(ref _dataVersion, version) != version;
        if (changed
            || await _cacheService.GetCacheAsync(VersionCacheKey).ConfigureAwait(false) is null)
            await _cacheService.SetCacheAsync(VersionCacheKey, version, TimeSpan.FromDays(365), generation)
                .ConfigureAwait(false);
        if (changed) DataVersionChanged?.Invoke(version);
        return version;
    }

    private async Task<T> SendJsonAsync<T>(string path, CancellationToken ct,
        Func<string, string, T>? notFound = null) where T : MetadataResponse
    {
        try
        {
            var generation = _cacheService.CaptureGeneration();
            using var response = await _httpClient.GetAsync(path, ct).ConfigureAwait(false);
            var version = await ObserveVersionAsync(response, generation).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound && notFound is not null)
                return notFound(json, version);
            response.EnsureSuccessStatusCode();
            var result = JsonSerializer.Deserialize<T>(json, JsonOptions)
                ?? throw new JsonException("资料服务返回空 JSON。");
            // 缓存键的版本只来自响应头，不从请求前推测或信任正文中的版本。
            result.DataVersion = version;
            return result;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException
            or OperationCanceledException or IOException or JsonException)
        {
            throw new MetadataServiceException("资料服务请求失败。", ex);
        }
    }

    private static bool IsServiceFailure(Exception ex) => ex is MetadataServiceException
        or HttpRequestException or OperationCanceledException or IOException or JsonException;

    private async Task<T?> ReadCacheAsync<T>(string key, bool expired = false) where T : class
    {
        var json = expired ? await _cacheService.GetCacheAllowExpiredAsync(key).ConfigureAwait(false)
            : await _cacheService.GetCacheAsync(key).ConfigureAwait(false);
        if (json is null) return null;
        try { return JsonSerializer.Deserialize<T>(json, JsonOptions); }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Removing invalid metadata cache {Key}", key);
            await _cacheService.RemoveCacheIfMatchesAsync(key, json).ConfigureAwait(false);
            return null;
        }
    }

    private async Task<T> CachedAsync<T>(string key, TimeSpan expiration, bool versioned,
        Func<Task<T>> fetch, CancellationToken ct) where T : MetadataResponse
    {
        ct.ThrowIfCancellationRequested();
        if (versioned) await EnsureVersionAsync(ct).ConfigureAwait(false);
        string CacheKey() => versioned ? VersionedKey(key, _dataVersion!) : key;
        var cached = await ReadCacheAsync<T>(CacheKey()).ConfigureAwait(false);
        if (cached is not null) return cached;

        CacheMissGate gate;
        lock (_cacheMissGateLock)
        {
            if (!_cacheMissGates.TryGetValue(key, out gate!))
                _cacheMissGates.Add(key, gate = new CacheMissGate());
            gate.Leases++;
        }
        var entered = false;
        try
        {
            await gate.Semaphore.WaitAsync(ct).ConfigureAwait(false);
            entered = true;
            var lookupKey = CacheKey();
            cached = await ReadCacheAsync<T>(lookupKey).ConfigureAwait(false);
            if (cached is not null) return cached;
            var generation = _cacheService.CaptureGeneration();
            T data;
            try { data = await fetch().ConfigureAwait(false); }
            catch (Exception ex) when (!ct.IsCancellationRequested && IsServiceFailure(ex))
            {
                cached = await ReadCacheAsync<T>(lookupKey, expired: true).ConfigureAwait(false);
                if (cached is null && versioned)
                {
                    var lastKey = await _cacheService.GetCacheAllowExpiredAsync(key + ":last").ConfigureAwait(false);
                    if (lastKey is not null)
                        cached = await ReadCacheAsync<T>(lastKey, expired: true).ConfigureAwait(false);
                }
                if (cached is null) throw;
                _logger.LogWarning(ex, "Using stale metadata cache {Key}", key);
                return cached;
            }
            // 404 不缓存七天，避免刚公布的作品长期不可见。
            if (data is not MetadataDetail { Data: null })
            {
                var savedKey = versioned ? VersionedKey(key, data.DataVersion) : key;
                await _cacheService.SetCacheAsync(savedKey, JsonSerializer.Serialize(data, JsonOptions), expiration, generation)
                    .ConfigureAwait(false);
                if (versioned)
                    await _cacheService.SetCacheAsync(key + ":last", savedKey, expiration, generation).ConfigureAwait(false);
            }
            return data;
        }
        finally
        {
            if (entered) gate.Semaphore.Release();
            lock (_cacheMissGateLock)
            {
                if (--gate.Leases == 0)
                {
                    _cacheMissGates.Remove(key);
                    gate.Semaphore.Dispose();
                }
            }
        }
    }

    private sealed class CacheMissGate
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int Leases { get; set; }
    }
}
