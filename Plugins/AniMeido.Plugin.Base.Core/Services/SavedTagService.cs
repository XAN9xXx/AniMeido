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
        private readonly SemaphoreSlim _writeGate = new(1, 1);

        public SavedTagService(SqliteConnectionFactory dbFactory)
        {
            _dbFactory = dbFactory;
        }

        public Task<List<string>> GetAllSavedTagsAsync()
            => Task.Run(async () =>
            {
                using var connection = await _dbFactory.OpenAsync().ConfigureAwait(false);
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT TagName FROM saved_tags ORDER BY TagName";
                var result = new List<string>();
                using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    result.Add(reader.GetString(0));
                }
                return result;
            });

        public Task<List<SavedTagState>> GetSavedTagStatesAsync()
            => Task.Run(async () =>
            {
                using var connection = await _dbFactory.OpenAsync().ConfigureAwait(false);
                return await GetSavedTagStatesAsync(connection, null).ConfigureAwait(false);
            });

        internal static async Task<List<SavedTagState>> GetSavedTagStatesAsync(
            SqliteConnection connection, SqliteTransaction? transaction)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT TagName,Invalid FROM saved_tags ORDER BY TagName";
            using var reader = await command.ExecuteReaderAsync();
            var result = new List<SavedTagState>();
            while (await reader.ReadAsync())
            {
                result.Add(new SavedTagState(reader.GetString(0), reader.GetBoolean(1)));
            }
            return result;
        }

        public Task<bool> IsTagSavedAsync(string tagName)
            => Task.Run(async () =>
            {
                using var connection = await _dbFactory.OpenAsync().ConfigureAwait(false);
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM saved_tags WHERE TagName = @tag";
                command.Parameters.AddWithValue("@tag", tagName);
                var count = Convert.ToInt32(await command.ExecuteScalarAsync().ConfigureAwait(false));
                return count > 0;
            });

        public Task SaveTagAsync(string tagName)
            => WriteTagAsync(tagName, "INSERT OR IGNORE INTO saved_tags (TagName) VALUES (@tag)");

        public Task RemoveTagAsync(string tagName)
            => WriteTagAsync(tagName, "DELETE FROM saved_tags WHERE TagName = @tag");

        private async Task WriteTagAsync(string tagName, string sql)
        {
            using var operation = _dbFactory.Access.BeginWrite();
            // 先登记写入顺序，再调度后台 SQLite 操作。
            await _writeGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await Task.Run(async () =>
                {
                    using var connection = await _dbFactory.OpenAsync().ConfigureAwait(false);
                    using var command = connection.CreateCommand();
                    command.CommandText = sql;
                    command.Parameters.AddWithValue("@tag", tagName);
                    await command.ExecuteNonQueryAsync().ConfigureAwait(false);
                }).ConfigureAwait(false);
            }
            finally
            {
                _writeGate.Release();
            }
        }
    }

    public sealed record SavedTagState(string Name, bool Invalid);
}
