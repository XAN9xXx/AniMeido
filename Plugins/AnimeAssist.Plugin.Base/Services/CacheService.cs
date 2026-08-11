using AniMeido.Contracts;
using Microsoft.Data.Sqlite;
using System.Collections.Concurrent;

namespace AniMeido.Plugin.Base.Services
{
    /// <summary>
    /// 二级缓存服务：内存（热数据）+ SQLite（持久化）。
    /// 内存缓存条目在写入 SQLite 后加入内存，并发读取时优先命中内存，避免 SQLite I/O。
    /// 内存缓存使用过期时间，不会无限增长。
    ///
    /// SQLite 操作每次使用独立连接（与 TrackingService 等模式一致），避免单个长期连接上的并发冲突。
    /// </summary>
    public class CacheService
    {
        private readonly SqliteConnectionFactory _dbFactory;
        private readonly SemaphoreSlim _mutationGate = new(1, 1);
        private long _generation;
        private volatile bool _isClearing;

        // 内存缓存：key → (json数据, 过期UTC)
        private readonly ConcurrentDictionary<string, (string data, DateTime expiresAt)> _memoryCache = new();

        public CacheService(SqliteConnectionFactory dbFactory)
        {
            _dbFactory = dbFactory;
            // 启动时自动清理过期缓存（CleanExpiredAsync 内部有 try-catch）
            _ = CleanExpiredAsync();
        }

        public async Task SetCacheAsync(string key, string data, TimeSpan expiration)
            => await SetCacheAsync(
                key,
                data,
                expiration,
                CaptureGeneration());

        internal long CaptureGeneration()
            => Interlocked.Read(ref _generation);

        internal async Task SetCacheAsync(
            string key,
            string data,
            TimeSpan expiration,
            long expectedGeneration)
        {
            await _mutationGate.WaitAsync();
            try
            {
                if (expectedGeneration != CaptureGeneration())
                {
                    return;
                }

                var expiresAt = DateTime.UtcNow.Add(expiration);

                // 先写入 SQLite（每次使用独立连接）
                using var connection = await _dbFactory.OpenAsync();
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

                await command.ExecuteNonQueryAsync();

                // 再写入内存缓存。ClearAllCacheAsync 与本段互斥，
                // 因而清空操作返回后不会被旧请求重新填充。
                _memoryCache[key] = (data, expiresAt);
            }
            finally
            {
                _mutationGate.Release();
            }
        }

        public Task<string?> GetCacheAsync(string key)
        {
            // 优先查内存缓存
            if (_memoryCache.TryGetValue(key, out var entry))
            {
                if (entry.expiresAt > DateTime.UtcNow)
                    return Task.FromResult<string?>(entry.data);

                // 已过期，移除内存条目
                _memoryCache.TryRemove(key, out _);
            }

            // 内存无命中或已过期，查 SQLite
            return GetFromSqliteAsync(key, onlyValid: true);
        }

        /// <summary>
        /// 获取缓存数据，即使已过期也返回（用于网络失败降级）。
        /// </summary>
        public async Task<string?> GetCacheAllowExpiredAsync(string key)
        {
            // 即使过期也先查内存
            if (_memoryCache.TryGetValue(key, out var entry))
                return entry.data;

            return await GetFromSqliteAsync(key, onlyValid: false);
        }

        /// <summary>
        /// 删除指定缓存键（内存 + SQLite）。
        /// </summary>
        public async Task RemoveCacheAsync(string key)
        {
            await _mutationGate.WaitAsync();
            try
            {
                _memoryCache.TryRemove(key, out _);

                using var connection = await _dbFactory.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM cache WHERE CacheKey = @key";
                command.Parameters.AddWithValue("@key", key);
                await command.ExecuteNonQueryAsync();
            }
            finally
            {
                _mutationGate.Release();
            }
        }

        /// <summary>
        /// 清空所有缓存数据（内存 + SQLite + 图片文件）。
        /// </summary>
        public async Task ClearAllCacheAsync()
        {
            await _mutationGate.WaitAsync();
            try
            {
                _isClearing = true;
                Interlocked.Increment(ref _generation);
                _memoryCache.Clear();

                using var connection = await _dbFactory.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM cache";
                await command.ExecuteNonQueryAsync();

                ImageCacheHelper.ClearAll();
            }
            finally
            {
                _isClearing = false;
                _mutationGate.Release();
            }
        }

        /// <summary>
        /// 获取当前缓存条目数和预估大小（KB），含内存缓存条目和图片缓存。
        /// </summary>
        public async Task<(int count, double sizeKB)> GetCacheStatsAsync()
        {
            using var connection = await _dbFactory.OpenAsync();

            var countCmd = connection.CreateCommand();
            countCmd.CommandText = "SELECT COUNT(*) FROM cache";
            var count = Convert.ToInt32(await countCmd.ExecuteScalarAsync());

            var sizeCmd = connection.CreateCommand();
            sizeCmd.CommandText = "SELECT COALESCE(SUM(LENGTH(Data)), 0) FROM cache";
            var totalBytes = Convert.ToInt64(await sizeCmd.ExecuteScalarAsync());
            var dbSizeKB = totalBytes / 1024.0;

            var (_, imgSizeMB) = ImageCacheHelper.GetCacheStats();
            var totalSizeKB = dbSizeKB + imgSizeMB * 1024.0;

            return (count, totalSizeKB);
        }

        /// <summary>
        /// 清理所有已过期的缓存条目。
        /// </summary>
        public async Task CleanExpiredAsync()
        {
            try
            {
                using var connection = await _dbFactory.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM cache WHERE ExpiresAt <= @now";
                command.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
                await command.ExecuteNonQueryAsync();

                // 同时清理内存中的过期条目
                var expiredKeys = _memoryCache
                    .Where(kvp => kvp.Value.expiresAt <= DateTime.UtcNow)
                    .Select(kvp => kvp.Key)
                    .ToList();
                foreach (var key in expiredKeys)
                    _memoryCache.TryRemove(key, out _);
            }
#pragma warning disable CA1031 // 后台缓存清理失败不应影响主流程
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CacheService] CleanExpiredAsync failed: {ex.Message}");
            }
#pragma warning restore CA1031
        }

        // ---- 私有辅助 ----

        private async Task<string?> GetFromSqliteAsync(string key, bool onlyValid)
        {
            var expectedGeneration = CaptureGeneration();
            await WaitForClearAsync();
            if (expectedGeneration != CaptureGeneration())
            {
                return null;
            }

            using var connection = await _dbFactory.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = onlyValid
                ? """
                    SELECT Data, ExpiresAt FROM cache
                    WHERE CacheKey = @key AND ExpiresAt > @now
                    """
                : """
                    SELECT Data, ExpiresAt FROM cache
                    WHERE CacheKey = @key
                    ORDER BY ExpiresAt DESC
                    LIMIT 1
                    """;
            command.Parameters.AddWithValue("@key", key);
            if (onlyValid)
                command.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));

            using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                if (expectedGeneration != CaptureGeneration())
                {
                    return null;
                }

                var data = reader.GetString(0);
                var expiresAtStr = reader.GetString(1);
                if (DateTime.TryParse(expiresAtStr, out var expiresAt))
                {
                    // 回填内存缓存（即使已过期，也允许降级时命中）
                    _memoryCache[key] = (data, expiresAt);
                }
                return data;
            }
            return null;
        }

        private async Task WaitForClearAsync()
        {
            if (!_isClearing)
            {
                return;
            }

            await _mutationGate.WaitAsync();
            _mutationGate.Release();
        }
    }
}

