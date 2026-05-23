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
    }
}
