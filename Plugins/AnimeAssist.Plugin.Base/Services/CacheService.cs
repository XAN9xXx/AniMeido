using Microsoft.Data.Sqlite;

namespace AniMeido.Plugin.Base.Services
{
    internal class CacheService
    {
        private readonly string _connectionString;



        public CacheService(string dbPath)
        {
            _connectionString = $"Data Source={dbPath}";
        }



        public async Task SetCacheAsync(string key, string data, TimeSpan expiration)
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var command = connection.CreateCommand();
            command.CommandText = """
                INSERT OR REPLACE INTO cache (CacheKey, Data, ExpiresAt)
                VALUES (@key, @data, @expiresAt)
                """;
            command.Parameters.AddWithValue("@key", key);
            command.Parameters.AddWithValue("@data", data);
            command.Parameters.AddWithValue("@expiresAt", DateTime.UtcNow.Add(expiration).ToString("O"));

            await command.ExecuteNonQueryAsync();
        }

        public async Task<string?> GetCacheAsync(string key)
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var command = connection.CreateCommand();
            command.CommandText = """
                SELECT Data FROM cache
                WHERE CacheKey = @key AND ExpiresAt > @now
                """;
            command.Parameters.AddWithValue("@key", key);
            command.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));

            var result = await command.ExecuteScalarAsync();
            return result as string;
        }

        /// <summary>
        /// 清空所有缓存数据。
        /// </summary>
        public async Task ClearAllCacheAsync()
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM cache";
            await command.ExecuteNonQueryAsync();
        }

        /// <summary>
        /// 获取当前缓存条目数和预估大小（KB）。
        /// </summary>
        public async Task<(int count, double sizeKB)> GetCacheStatsAsync()
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var countCmd = connection.CreateCommand();
            countCmd.CommandText = "SELECT COUNT(*) FROM cache";
            var count = Convert.ToInt32(await countCmd.ExecuteScalarAsync());

            var sizeCmd = connection.CreateCommand();
            // 近似：Data 字段的字符数 * 2 作为字节估算
            sizeCmd.CommandText = "SELECT COALESCE(SUM(LENGTH(Data)), 0) FROM cache";
            var totalChars = Convert.ToInt32(await sizeCmd.ExecuteScalarAsync());
            var sizeKB = totalChars * 2.0 / 1024.0;

            return (count, sizeKB);
        }
    }
}
