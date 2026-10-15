using AniMeido.Contracts;
using Microsoft.Data.Sqlite;

namespace AniMeido.Plugin.Base.Services
{
    /// <summary>
    /// 数据表: saved_tags (TagName) — Tag 名称全局唯一。
    /// 收藏一个 Tag 后，可配合 Bangumi API 搜索所有带此 Tag 的番剧。
    /// </summary>
    public class SavedTagService
    {
        private readonly SqliteConnectionFactory _dbFactory;

        public SavedTagService(SqliteConnectionFactory dbFactory)
        {
            _dbFactory = dbFactory;
        }

        public async Task<List<string>> GetAllSavedTagsAsync()
        {
            using var conn = await _dbFactory.OpenAsync();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT TagName FROM saved_tags ORDER BY TagName";
            var list = new List<string>();
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                list.Add(reader.GetString(0));
            return list;
        }

        public async Task<bool> IsTagSavedAsync(string tagName)
        {
            using var conn = await _dbFactory.OpenAsync();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM saved_tags WHERE TagName = @tag";
            cmd.Parameters.AddWithValue("@tag", tagName);
            var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            return count > 0;
        }

        public async Task SaveTagAsync(string tagName)
        {
            using var conn = await _dbFactory.OpenAsync();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO saved_tags (TagName) VALUES (@tag)";
            cmd.Parameters.AddWithValue("@tag", tagName);
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task RemoveTagAsync(string tagName)
        {
            using var conn = await _dbFactory.OpenAsync();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM saved_tags WHERE TagName = @tag";
            cmd.Parameters.AddWithValue("@tag", tagName);
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
