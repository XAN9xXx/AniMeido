using Microsoft.Data.Sqlite;
using AniMeido.Contracts.Models;

namespace AniMeido.App.Services
{
    public class DatabaseService
    {
        private readonly string _connectionString;



        public DatabaseService() 
        {
            string dbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AniMeido", "AniMeido.db");
            string dir = Path.GetDirectoryName(dbPath)!;
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            _connectionString = $"Data Source={dbPath}";
        }



        public async Task InitializeAsync()
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS tracking(
                    AnimeID   INTEGER PRIMARY KEY,
                    Status    INTEGER NOT NULL,
                    UpdatedAt TEXT NOT NULL
                )
            """;
            await command.ExecuteNonQueryAsync();

            command.CommandText = """
                CREATE TABLE IF NOT EXISTS cache(
                    CacheKey  TEXT PRIMARY KEY,
                    Data      TEXT NOT NULL,
                    ExpiresAt TEXT NOT NULL
                )
            """;
            await command.ExecuteNonQueryAsync();
        }



        public async Task SetTrackingStatusAsync(int animeId, AnimeTrackingStatus status)
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var command = connection.CreateCommand();
            command.CommandText = """
                INSERT OR REPLACE INTO tracking (AnimeId, Status, UpdatedAt)
                VALUES (@animeId, @status, @updatedAt)
                """;
            command.Parameters.AddWithValue("@animeId", animeId);
            command.Parameters.AddWithValue("@status", (int)status);
            command.Parameters.AddWithValue("@updatedAt", DateTime.Now.ToString("O"));

            await command.ExecuteNonQueryAsync();
        }

        public async Task<AnimeTrackingStatus?> GetTrackingStatusAsync(int animeId)
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var command = connection.CreateCommand();
            command.CommandText = "SELECT Status FROM tracking WHERE AnimeId = @animeId";
            command.Parameters.AddWithValue("@animeId", animeId);

            var result = await command.ExecuteScalarAsync();
            if (result is null) return null;

            return (AnimeTrackingStatus)(int)result;
        }

        public async Task<List<int>> GetAnimeIdsByStatusAsync(AnimeTrackingStatus status)
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var command = connection.CreateCommand();
            command.CommandText = "SELECT AnimeId FROM tracking WHERE Status = @status";
            command.Parameters.AddWithValue("@status", (int)status);

            var list = new List<int>();
            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(reader.GetInt32(0));
            }
            return list;
        }

        public async Task RemoveTrackingAsync(int animeId)
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM tracking WHERE AnimeId = @animeId";
            command.Parameters.AddWithValue("@animeId", animeId);

            await command.ExecuteNonQueryAsync();
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
