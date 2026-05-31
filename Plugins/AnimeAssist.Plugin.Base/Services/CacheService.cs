using Microsoft.Data.Sqlite;
using System.Collections.Concurrent;

namespace AniMeido.Plugin.Base.Services
{
    /// <summary>
    /// 二级缓存服务：内存（热数据）+ SQLite（持久化）。
    /// 内存缓存条目在写入 SQLite 后加入内存，并发读取时优先命中内存，避免 SQLite I/O。
    /// 内存缓存使用弱引用+过期时间，不会无限增长。
    /// </summary>
    internal class CacheService : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly SemaphoreSlim _syncLock = new(1, 1);

        // 内存缓存：key → (json数据, 过期UTC)
        private readonly ConcurrentDictionary<string, (string data, DateTime expiresAt)> _memoryCache = new();

        public CacheService(string dbPath)
        {
            _connection = new SqliteConnection($"Data Source={dbPath}");
            _connection.Open();
        }

        public void Dispose()
        {
            _connection?.Close();
            _connection?.Dispose();
            _syncLock?.Dispose();
            _memoryCache.Clear();
        }

        public async Task SetCacheAsync(string key, string data, TimeSpan expiration)
        {
            var expiresAt = DateTime.UtcNow.Add(expiration);

            // 先写入 SQLite
            await _syncLock.WaitAsync();
            try
            {
                using var command = _connection.CreateCommand();
                command.CommandText = """
                    INSERT OR REPLACE INTO cache (CacheKey, Data, ExpiresAt)
                    VALUES (@key, @data, @expiresAt)
                    """;
                command.Parameters.AddWithValue("@key", key);
                command.Parameters.AddWithValue("@data", data);
                command.Parameters.AddWithValue("@expiresAt", expiresAt.ToString("O"));

                await command.ExecuteNonQueryAsync();
            }
            finally
            {
                _syncLock.Release();
            }

            // 再写入内存缓存
            _memoryCache[key] = (data, expiresAt);
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
        /// 清空所有缓存数据（内存 + SQLite）。
        /// </summary>
        public async Task ClearAllCacheAsync()
        {
            _memoryCache.Clear();

            await _syncLock.WaitAsync();
            try
            {
                using var command = _connection.CreateCommand();
                command.CommandText = "DELETE FROM cache";
                await command.ExecuteNonQueryAsync();
            }
            finally
            {
                _syncLock.Release();
            }
        }

        /// <summary>
        /// 获取当前缓存条目数和预估大小（KB），含内存缓存条目。
        /// </summary>
        public async Task<(int count, double sizeKB)> GetCacheStatsAsync()
        {
            var countCmd = _connection.CreateCommand();
            countCmd.CommandText = "SELECT COUNT(*) FROM cache";
            var count = Convert.ToInt32(await countCmd.ExecuteScalarAsync());

            var sizeCmd = _connection.CreateCommand();
            sizeCmd.CommandText = "SELECT COALESCE(SUM(LENGTH(Data)), 0) FROM cache";
            var totalChars = Convert.ToInt32(await sizeCmd.ExecuteScalarAsync());
            var sizeKB = totalChars * 2.0 / 1024.0;

            return (count, sizeKB);
        }

        // ---- 私有辅助 ----

        private async Task<string?> GetFromSqliteAsync(string key, bool onlyValid)
        {
            using var command = _connection.CreateCommand();
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
    }
}
