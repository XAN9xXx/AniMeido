using AniMeido.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;

namespace AniMeido.Plugin.Base.Services
{
    /// <summary>
    /// 二级缓存服务：内存（热数据）+ SQLite（持久化）。
    /// 内存缓存条目在写入 SQLite 后加入内存，并发读取时优先命中内存，避免 SQLite I/O。
    /// 过期数据不作为有效缓存返回，过期后再保留 30 天供网络失败时降级使用。
    ///
    /// SQLite 操作每次使用独立连接（与 TrackingService 等模式一致），避免单个长期连接上的并发冲突。
    /// </summary>
    public class CacheService
    {
        private static readonly TimeSpan ExpiredRetention = TimeSpan.FromDays(30);

        private readonly SqliteConnectionFactory _dbFactory;
        private readonly ILogger<CacheService> _logger;
        private readonly Action? _clearImages;
        private readonly Func<double>? _getImageSizeKB;
        private readonly SemaphoreSlim _mutationGate = new(1, 1);
        private long _generation;

        private readonly ConcurrentDictionary<string, (string data, DateTime expiresAt)> _memoryCache = new();

        public CacheService(
            SqliteConnectionFactory dbFactory,
            Action? clearImages = null,
            Func<double>? getImageSizeKB = null)
            : this(dbFactory, clearImages, getImageSizeKB, NullLogger<CacheService>.Instance)
        {
        }

        internal CacheService(
            SqliteConnectionFactory dbFactory,
            Action? clearImages,
            Func<double>? getImageSizeKB,
            ILogger<CacheService> logger)
        {
            _dbFactory = dbFactory;
            _clearImages = clearImages;
            _getImageSizeKB = getImageSizeKB;
            _logger = logger;
        }

        public async Task SetCacheAsync(string key, string data, TimeSpan expiration)
            => await SetCacheAsync(
                key,
                data,
                expiration,
                CaptureGeneration()).ConfigureAwait(false);

        internal long CaptureGeneration()
            => Interlocked.Read(ref _generation);

        internal async Task SetCacheAsync(
            string key,
            string data,
            TimeSpan expiration,
            long expectedGeneration)
        {
            using var operation = _dbFactory.Access.BeginWrite();
            await _mutationGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (expectedGeneration != CaptureGeneration())
                {
                    return;
                }

                var expiresAt = DateTime.UtcNow.Add(expiration);

                await Task.Run(async () =>
                {
                    using var connection = await _dbFactory.OpenAsync().ConfigureAwait(false);
                    using var command = connection.CreateCommand();
                    command.CommandText = """
                        INSERT OR REPLACE INTO cache (CacheKey, Data, ExpiresAt)
                        VALUES (@key, @data, @expiresAt)
                        """;
                    command.Parameters.AddWithValue("@key", key);
                    command.Parameters.AddWithValue("@data", data);
                    command.Parameters.AddWithValue(
                        "@expiresAt",
                        expiresAt.ToString("O"));

                    await command.ExecuteNonQueryAsync().ConfigureAwait(false);

                    // SQLite 写入成功后再写入内存缓存。ClearAllCacheAsync 与本段互斥，
                    // 因而清空操作返回后不会被旧请求重新填充。
                    _memoryCache[key] = (data, expiresAt);
                }).ConfigureAwait(false);
            }
            finally
            {
                _mutationGate.Release();
            }
        }

        public Task<string?> GetCacheAsync(string key)
        {
            if (_memoryCache.TryGetValue(key, out var entry))
            {
                var now = DateTime.UtcNow;
                if (entry.expiresAt > now)
                    return Task.FromResult<string?>(entry.data);

                // 过期不等于删除：保留期内仍留在内存，供离线读取使用。
                if (entry.expiresAt <= now.Subtract(ExpiredRetention))
                    _memoryCache.TryRemove(KeyValuePair.Create(key, entry));
            }

            return GetFromSqliteAsync(key, onlyValid: true);
        }

        /// <summary>
        /// 获取仍在保留期内的缓存数据，即使已过期也可用于网络失败降级。
        /// </summary>
        public async Task<string?> GetCacheAllowExpiredAsync(string key)
        {
            if (_memoryCache.TryGetValue(key, out var entry))
            {
                if (entry.expiresAt > DateTime.UtcNow.Subtract(ExpiredRetention))
                    return entry.data;

                // 只移除观察到的旧值，不误删并发写入的同名新缓存。
                _memoryCache.TryRemove(KeyValuePair.Create(key, entry));
            }

            return await GetFromSqliteAsync(key, onlyValid: false).ConfigureAwait(false);
        }

        public Task RemoveCacheAsync(string key)
            => RemoveCacheCoreAsync(key, observedData: null);

        internal Task RemoveCacheIfMatchesAsync(string key, string observedData)
            => RemoveCacheCoreAsync(key, observedData);

        private async Task RemoveCacheCoreAsync(string key, string? observedData)
        {
            using var operation = _dbFactory.Access.BeginWrite();
            await _mutationGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (observedData is null)
                {
                    _memoryCache.TryRemove(key, out _);
                }
                else if (_memoryCache.TryGetValue(key, out var entry)
                    && string.Equals(entry.data, observedData, StringComparison.Ordinal))
                {
                    _memoryCache.TryRemove(KeyValuePair.Create(key, entry));
                }

                await Task.Run(async () =>
                {
                    using var connection = await _dbFactory.OpenAsync().ConfigureAwait(false);
                    using var command = connection.CreateCommand();
                    command.CommandText = observedData is null
                        ? "DELETE FROM cache WHERE CacheKey = @key"
                        : "DELETE FROM cache WHERE CacheKey = @key AND Data = @observed";
                    command.Parameters.AddWithValue("@key", key);
                    if (observedData is not null)
                        command.Parameters.AddWithValue("@observed", observedData);
                    await command.ExecuteNonQueryAsync().ConfigureAwait(false);
                }).ConfigureAwait(false);
            }
            finally
            {
                _mutationGate.Release();
            }
        }

        /// <summary>
        /// 清空所有缓存数据（内存 + SQLite + 图片文件）。
        /// </summary>
        public Task ClearAllCacheAsync() => ClearCacheCoreAsync(clearImages: true);

        internal Task ClearMetadataForIdRemapAsync() => ClearCacheCoreAsync(clearImages: false);

        private async Task ClearCacheCoreAsync(bool clearImages)
        {
            using var operation = _dbFactory.Access.BeginWrite();
            await _mutationGate.WaitAsync().ConfigureAwait(false);
            try
            {
                Interlocked.Increment(ref _generation);
                _memoryCache.Clear();

                await Task.Run(async () =>
                {
                    using var connection = await _dbFactory.OpenAsync().ConfigureAwait(false);
                    using var command = connection.CreateCommand();
                    command.CommandText = "DELETE FROM cache";
                    await command.ExecuteNonQueryAsync().ConfigureAwait(false);

                    if (clearImages)
                        _clearImages?.Invoke();
                }).ConfigureAwait(false);
            }
            finally
            {
                _mutationGate.Release();
            }
        }

        /// <summary>
        /// 获取当前缓存条目数和预估大小（KB），含内存缓存条目和图片缓存。
        /// </summary>
        public Task<(int count, double sizeKB)> GetCacheStatsAsync()
            => Task.Run(async () =>
            {
                using var connection = await _dbFactory.OpenAsync().ConfigureAwait(false);

                using var countCommand = connection.CreateCommand();
                countCommand.CommandText = "SELECT COUNT(*) FROM cache";
                var count = Convert.ToInt32(await countCommand.ExecuteScalarAsync().ConfigureAwait(false));

                using var sizeCommand = connection.CreateCommand();
                sizeCommand.CommandText = "SELECT COALESCE(SUM(LENGTH(Data)), 0) FROM cache";
                var totalBytes = Convert.ToInt64(await sizeCommand.ExecuteScalarAsync().ConfigureAwait(false));
                var dbSizeKB = totalBytes / 1024.0;

                var totalSizeKB = dbSizeKB + (_getImageSizeKB?.Invoke() ?? 0);

                return (count, totalSizeKB);
            });

        /// <summary>
        /// 清理过期已满 30 天的缓存条目（内存 + SQLite），保留期内仍可离线兜底。
        /// </summary>
        public async Task CleanExpiredAsync()
        {
            try
            {
                using var operation = _dbFactory.Access.BeginWrite();
                await _mutationGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    await Task.Run(async () =>
                    {
                        using var connection = await _dbFactory.OpenAsync().ConfigureAwait(false);
                        using var command = connection.CreateCommand();
                        var retentionCutoff = DateTime.UtcNow.Subtract(ExpiredRetention);
                        command.CommandText = "DELETE FROM cache WHERE ExpiresAt <= @retentionCutoff";
                        command.Parameters.AddWithValue("@retentionCutoff", retentionCutoff.ToString("O"));
                        await command.ExecuteNonQueryAsync().ConfigureAwait(false);

                        // 内存与 SQLite 使用同一截止时间，只移除本次观察到的过期值。
                        foreach (var entry in _memoryCache)
                        {
                            if (entry.Value.expiresAt <= retentionCutoff)
                            {
                                _memoryCache.TryRemove(entry);
                            }
                        }
                    }).ConfigureAwait(false);
                }
                finally
                {
                    _mutationGate.Release();
                }
            }
            catch (PersonalDataBusyException)
            {
                // 导入独占期间不清理；下一次维护仍按原保留期处理。
            }
#pragma warning disable CA1031 // 后台缓存清理失败不应影响主流程
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "清理过期缓存失败，将在下次维护时重试");
            }
#pragma warning restore CA1031
        }

        private async Task<string?> GetFromSqliteAsync(string key, bool onlyValid)
        {
            var expectedGeneration = CaptureGeneration();
            await _mutationGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (expectedGeneration != CaptureGeneration())
                {
                    return null;
                }

                return await Task.Run(async () =>
                {
                    using var connection = await _dbFactory.OpenAsync().ConfigureAwait(false);
                    using var command = connection.CreateCommand();
                    command.CommandText = onlyValid
                        ? """
                            SELECT Data, ExpiresAt FROM cache
                            WHERE CacheKey = @key AND ExpiresAt > @now
                            """
                        : """
                            SELECT Data, ExpiresAt FROM cache
                            WHERE CacheKey = @key AND ExpiresAt > @retentionCutoff
                            ORDER BY ExpiresAt DESC
                            LIMIT 1
                            """;
                    command.Parameters.AddWithValue("@key", key);
                    if (onlyValid)
                        command.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
                    else
                        command.Parameters.AddWithValue("@retentionCutoff",
                            DateTime.UtcNow.Subtract(ExpiredRetention).ToString("O"));

                    using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
                    if (await reader.ReadAsync().ConfigureAwait(false))
                    {
                        var data = reader.GetString(0);
                        var expiresAtStr = reader.GetString(1);
                        if (DateTime.TryParse(
                            expiresAtStr,
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                            out var expiresAt))
                        {
                            // 回填内存缓存（即使已过期，也允许降级时命中）
                            _memoryCache[key] = (data, expiresAt);
                        }
                        return data;
                    }
                    return null;
                }).ConfigureAwait(false);
            }
            finally
            {
                _mutationGate.Release();
            }
        }
    }
}

