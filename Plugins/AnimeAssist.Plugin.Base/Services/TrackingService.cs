using AniMeido.Contracts.Models;
using AniMeido.Contracts;
using Microsoft.Data.Sqlite;

namespace AniMeido.Plugin.Base.Services
{
    public class TrackingService : ITrackingService
    {
        private readonly string _connectionString;



        public TrackingService(string dbPath)
        {
            _connectionString = $"Data Source={dbPath}";
        }



        public async Task SetStatusAsync(int animeId, AnimeTrackingStatus status)
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

        public async Task<AnimeTrackingStatus?> GetStatusAsync(int animeId)
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var command = connection.CreateCommand();
            command.CommandText = "SELECT Status FROM tracking WHERE AnimeId = @animeId";
            command.Parameters.AddWithValue("@animeId", animeId);

            var result = await command.ExecuteScalarAsync();
            if (result is null) return null;

            return (AnimeTrackingStatus)Convert.ToInt32(result);
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
                list.Add(Convert.ToInt32(reader.GetInt64(0)));
            }
            return list;
        }

        public async Task RemoveStatusAsync(int animeId)
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM tracking WHERE AnimeId = @animeId";
            command.Parameters.AddWithValue("@animeId", animeId);

            await command.ExecuteNonQueryAsync();
        }

    }
}
