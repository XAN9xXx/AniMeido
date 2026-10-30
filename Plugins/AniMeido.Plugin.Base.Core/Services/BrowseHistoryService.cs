using AniMeido.Contracts;
using Microsoft.Data.Sqlite;

namespace AniMeido.Plugin.Base.Services
{
    public class BrowseHistoryService
    {
        private readonly SqliteConnectionFactory _dbFactory;
        private readonly SemaphoreSlim _historyWriteGate = new(1, 1);

        public BrowseHistoryService(SqliteConnectionFactory dbFactory)
        {
            _dbFactory = dbFactory;
        }

        public Task RecordAsync(int animeId, string? titleSnapshot)
            => WriteHistoryAsync(async () =>
            {
                using var connection = await _dbFactory.OpenAsync().ConfigureAwait(false);
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO browse_history (AnimeID, TitleSnapshot, LastViewedAt, ViewCount)
                    VALUES (@id, @title, @now, 1)
                    ON CONFLICT(AnimeID) DO UPDATE SET
                        TitleSnapshot = COALESCE(@title, TitleSnapshot),
                        LastViewedAt = @now,
                        ViewCount = ViewCount + 1
                    """;
                command.Parameters.AddWithValue("@id", animeId);
                command.Parameters.AddWithValue("@title", titleSnapshot ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }, CancellationToken.None);

        public Task<List<(int AnimeId, string? Title, DateTime LastViewed, int ViewCount)>> GetHistoryAsync(
            int limit = 50,
            CancellationToken cancellationToken = default)
            // 整次 SQLite 查询与日期映射离开调用线程，避免同步锁等待卡住页面。
            => Task.Run(async () =>
            {
                using var connection =
                    await _dbFactory.OpenAsync(cancellationToken);
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT AnimeID, TitleSnapshot, LastViewedAt, ViewCount
                    FROM browse_history
                    ORDER BY LastViewedAt DESC
                    LIMIT @limit
                    """;
                command.Parameters.AddWithValue("@limit", limit);

                var results = new List<(int, string?, DateTime, int)>();
                using var reader =
                    await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    results.Add((
                        reader.GetInt32(0),
                        reader.IsDBNull(1) ? null : reader.GetString(1),
                        DateTime.Parse(reader.GetString(2)),
                        reader.GetInt32(3)
                    ));
                }
                return results;
            }, cancellationToken);

        public Task ClearAsync(CancellationToken cancellationToken = default)
            => WriteHistoryAsync(async () =>
            {
                using var connection =
                    await _dbFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM browse_history";
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }, cancellationToken);

        private async Task WriteHistoryAsync(Func<Task> write, CancellationToken cancellationToken)
        {
            using var operation = _dbFactory.Access.BeginWrite();
            // 先按提交顺序登记，避免后台调度让旧浏览记录越过后续清空。
            await _historyWriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await Task.Run(write, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _historyWriteGate.Release();
            }
        }
    }
}
