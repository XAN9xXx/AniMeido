using Microsoft.Data.Sqlite;

namespace AniMeido.Plugin.Base.Services
{
    /// <summary>
    /// 浏览记录服务：记录和查询用户查看过的番剧。
    /// 数据表 browse_history 由 App 层 DatabaseService 创建。
    /// </summary>
    public class BrowseHistoryService
    {
        private readonly string _connectionString;

        public BrowseHistoryService(string dbPath)
        {
            _connectionString = $"Data Source={dbPath}";
        }

        /// <summary>记录或更新番剧浏览记录。</summary>
        public async Task RecordAsync(int animeId, string? titleSnapshot)
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO browse_history (AnimeID, TitleSnapshot, LastViewedAt, ViewCount)
                VALUES (@id, @title, @now, 1)
                ON CONFLICT(AnimeID) DO UPDATE SET
                    TitleSnapshot = COALESCE(@title, TitleSnapshot),
                    LastViewedAt = @now,
                    ViewCount = ViewCount + 1
            """;
            cmd.Parameters.AddWithValue("@id", animeId);
            cmd.Parameters.AddWithValue("@title", titleSnapshot ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync();
        }

        /// <summary>获取浏览记录（最近 -N 条）。</summary>
        public async Task<List<(int AnimeId, string? Title, DateTime LastViewed, int ViewCount)>> GetHistoryAsync(int limit = 50)
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT AnimeID, TitleSnapshot, LastViewedAt, ViewCount
                FROM browse_history
                ORDER BY LastViewedAt DESC
                LIMIT @limit
            """;
            cmd.Parameters.AddWithValue("@limit", limit);

            var results = new List<(int, string?, DateTime, int)>();
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                results.Add((
                    reader.GetInt32(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    DateTime.Parse(reader.GetString(2)),
                    reader.GetInt32(3)
                ));
            }
            return results;
        }

        /// <summary>清空浏览记录。</summary>
        public async Task ClearAsync()
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM browse_history";
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
