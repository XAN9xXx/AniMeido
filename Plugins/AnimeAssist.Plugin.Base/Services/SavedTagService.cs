using Microsoft.Data.Sqlite;

namespace AniMeido.Plugin.Base.Services
{
    /// <summary>
    /// 收藏的 Bangumi Tag 管理服务。
    /// 数据表: saved_tags (AnimeId, TagName)
    /// </summary>
    public class SavedTagService
    {
        private readonly string _connectionString;

        public SavedTagService(string dbPath)
        {
            _connectionString = $"Data Source={dbPath}";
        }

        /// <summary>获取番剧收藏的 Tag 列表。</summary>
        public async Task<List<string>> GetSavedTagsAsync(int animeId)
        {
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT TagName FROM saved_tags WHERE AnimeId = @id ORDER BY TagName";
            cmd.Parameters.AddWithValue("@id", animeId);
            var list = new List<string>();
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                list.Add(reader.GetString(0));
            return list;
        }

        /// <summary>收藏一个 Tag。</summary>
        public async Task SaveTagAsync(int animeId, string tagName)
        {
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO saved_tags (AnimeId, TagName) VALUES (@id, @tag)";
            cmd.Parameters.AddWithValue("@id", animeId);
            cmd.Parameters.AddWithValue("@tag", tagName);
            await cmd.ExecuteNonQueryAsync();
        }

        /// <summary>取消收藏一个 Tag。</summary>
        public async Task RemoveTagAsync(int animeId, string tagName)
        {
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM saved_tags WHERE AnimeId = @id AND TagName = @tag";
            cmd.Parameters.AddWithValue("@id", animeId);
            cmd.Parameters.AddWithValue("@tag", tagName);
            await cmd.ExecuteNonQueryAsync();
        }

        /// <summary>获取所有收藏了指定 Tag 的 AnimeId。</summary>
        public async Task<List<int>> GetAnimeIdsByTagAsync(string tagName)
        {
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT AnimeId FROM saved_tags WHERE TagName = @tag ORDER BY AnimeId";
            cmd.Parameters.AddWithValue("@tag", tagName);
            var list = new List<int>();
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                list.Add(Convert.ToInt32(reader.GetInt64(0)));
            return list;
        }

        /// <summary>获取所有收藏的 Tag 名称及计数。</summary>
        public async Task<List<(string TagName, int Count)>> GetAllSavedTagsAsync()
        {
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT TagName, COUNT(*) FROM saved_tags GROUP BY TagName ORDER BY TagName";
            var list = new List<(string, int)>();
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                list.Add((reader.GetString(0), Convert.ToInt32(reader.GetInt64(1))));
            return list;
        }
    }
}
