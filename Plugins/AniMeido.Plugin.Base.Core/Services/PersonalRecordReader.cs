using AniMeido.Plugin.Base.Models;
using Microsoft.Data.Sqlite;
using System.Globalization;

namespace AniMeido.Plugin.Base.Services;

/// <summary>共享的个人记录快照读取；不协调业务写入，也不发送变更通知。</summary>
internal static class PersonalRecordReader
{
    private static readonly (string Table, string Column)[] TitleSources =
    [
        ("anime_archives", "TitleSnapshot"),
        ("anime_plans", "TitleSnapshot"),
        ("browse_history", "TitleSnapshot"),
        ("manual_watch_events", "TitleSnapshot"),
        ("recommendation_hidden_anime", "TitleSnapshot"),
        ("screenshots", "AnimeTitle"),
    ];

    internal static async Task<IReadOnlyList<int>> ReadAnimeIdsAsync(
        SqliteConnection connection, SqliteTransaction? transaction, CancellationToken ct, int? requestedId = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // 每张表只返回不同的身份，不读取感想正文、播放字段或截图路径。
        var filter = requestedId is null ? "" : " AND AnimeId=@id";
        command.CommandText = string.Join(" UNION ALL ", PublicIdMigrationService.WorkTables
            .Select(table => $"SELECT DISTINCT AnimeId FROM {table} WHERE AnimeId IS NOT NULL AND AnimeId<>0{filter}"));
        if (requestedId is { } id)
            command.Parameters.AddWithValue("@id", id);
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var ids = new HashSet<int>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            ids.Add(reader.GetInt32(0));
        return ids.ToList();
    }

    internal static async Task<IReadOnlyDictionary<int, string>> ReadTitlesAsync(
        SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = string.Join(" UNION ALL ", TitleSources.Select((source, index) =>
            $"SELECT AnimeId, {source.Column} AS Title, {index} AS SourceOrder, rowid AS RowOrder " +
            $"FROM {source.Table} WHERE AnimeId IS NOT NULL AND AnimeId<>0 AND length({source.Column})>0"))
            + " ORDER BY SourceOrder, RowOrder";
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var titles = new Dictionary<int, string>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            titles.TryAdd(reader.GetInt32(0), reader.GetString(1));
        return titles;
    }

    internal static async Task<IReadOnlyDictionary<int, IReadOnlyDictionary<string, int>>> ReadPendingCountsAsync(
        SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH pending(AnimeId) AS (
                SELECT LocalId FROM legacy_anime_refs UNION SELECT AnimeId FROM public_id_issues
            )
            """ + string.Join(" UNION ALL ", PublicIdMigrationService.WorkTables.Select(table =>
                $"SELECT AnimeId, '{table}', COUNT(*) FROM {table} WHERE AnimeId IN (SELECT AnimeId FROM pending) GROUP BY AnimeId"));
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var counts = new Dictionary<int, Dictionary<string, int>>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var animeId = reader.GetInt32(0);
            if (!counts.TryGetValue(animeId, out var tables))
                counts[animeId] = tables = [];
            tables.Add(reader.GetString(1), reader.GetInt32(2));
        }
        return counts.ToDictionary(pair => pair.Key, pair => (IReadOnlyDictionary<string, int>)pair.Value);
    }

    internal static IEnumerable<int> GetAnimeIds(MigrationInput input) => input.Tables.Values.SelectMany(rows => rows)
        .Select(r => MigrationInput.Id(r.GetValueOrDefault("AnimeId"))).Where(id => id != 0).Distinct();

    internal static string GetTitle(MigrationInput input, int id) => input.Titles.GetValueOrDefault(id)
        ?? AnimeTitleFallback.Untitled;

    internal static async Task<MigrationInput> ReadSnapshotAsync(SqliteConnection connection, SqliteTransaction? tx, CancellationToken ct)
    {
        var input = new MigrationInput();
        foreach (var table in PublicIdMigrationService.WorkTables)
        {
            using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = $"SELECT * FROM {table}";
            using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var rows = new List<Dictionary<string, string?>>();
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var row = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < reader.FieldCount; i++)
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);
                rows.Add(row);
            }
            input.Tables[table] = rows;
        }
        // C2a 的标题收集只接收正数；此处也需要保留负数作品的个人快照。
        foreach (var (table, column) in TitleSources)
            foreach (var row in input.Rows(table))
                if (MigrationInput.Id(row["AnimeId"]) is not 0 and var id && row.GetValueOrDefault(column) is { Length: > 0 } title)
                    input.Titles.TryAdd(id, title);
        return input;
    }
}
