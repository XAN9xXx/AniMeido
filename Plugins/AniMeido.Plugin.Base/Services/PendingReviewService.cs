using AniMeido.Plugin.Base.Models;
using Microsoft.Data.Sqlite;
using System.Globalization;

namespace AniMeido.Plugin.Base.Services;

public sealed class PendingReviewService(SqliteConnectionFactory db, PersonalIdRemapService remap)
{
    public async Task<IReadOnlyList<PendingReviewItem>> GetItemsAsync(CancellationToken ct = default)
    {
        using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
        using var tx = connection.BeginTransaction();
        var records = await ReadRecordsAsync(connection, tx, ct).ConfigureAwait(false);
        using var command = connection.CreateCommand(); command.Transaction = tx;
        command.CommandText = """
            SELECT LocalId,LegacyBangumiId,Reason,NULL,TitleSnapshot,KeptAt FROM legacy_anime_refs
            UNION ALL SELECT AnimeId,NULL,Kind,MergedInto,TitleSnapshot,KeptAt FROM public_id_issues
            ORDER BY 1
            """;
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var result = new List<PendingReviewItem>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var id = reader.GetInt32(0);
            var counts = new Dictionary<string, int>();
            foreach (var table in PublicIdMigrationService.WorkTables)
            {
                var count = records.Rows(table).Count(r => MigrationInput.Id(r["AnimeId"]) == id);
                if (count > 0) counts[table] = count;
            }
            int? mergedInto = reader.IsDBNull(3) ? null : reader.GetInt32(3);
            result.Add(new PendingReviewItem(id, reader.IsDBNull(1) ? null : reader.GetInt32(1),
                reader.IsDBNull(4) ? Title(records, id) : reader.GetString(4), reader.GetString(2),
                mergedInto, mergedInto is { } target ? records.Titles.GetValueOrDefault(target) : null,
                reader.IsDBNull(5) ? null : reader.GetString(5), counts));
        }
        return result;
    }

    public async Task<IReadOnlyList<LocalAnimeTarget>> GetLocalTargetsAsync(CancellationToken ct = default)
    {
        using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
        var input = await ReadRecordsAsync(connection, null, ct).ConfigureAwait(false);
        return AllIds(input).Select(id => new LocalAnimeTarget(id, Title(input, id)))
            .OrderBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public Task SetKeptAsync(int id, bool kept, CancellationToken ct = default) => remap.RunTransactionAsync(async (connection, tx) =>
    {
        using var command = connection.CreateCommand(); command.Transaction = tx;
        command.CommandText = id < 0 ? "UPDATE legacy_anime_refs SET KeptAt=@at WHERE LocalId=@id"
            : "UPDATE public_id_issues SET KeptAt=@at WHERE AnimeId=@id";
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@at", kept ? DateTimeOffset.UtcNow.ToString("O") : (object)DBNull.Value);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }, ct);

    internal static IEnumerable<int> AllIds(MigrationInput input) => input.Tables.Values.SelectMany(rows => rows)
        .Select(r => MigrationInput.Id(r.GetValueOrDefault("AnimeId"))).Where(id => id != 0).Distinct();

    internal static string Title(MigrationInput input, int id) => input.Titles.GetValueOrDefault(id)
        ?? AnimeTitleFallback.Untitled;

    internal static async Task<MigrationInput> ReadRecordsAsync(SqliteConnection connection, SqliteTransaction? tx, CancellationToken ct)
    {
        var input = new MigrationInput();
        foreach (var table in PublicIdMigrationService.WorkTables)
        {
            using var command = connection.CreateCommand(); command.Transaction = tx; command.CommandText = $"SELECT * FROM {table}";
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
        foreach (var (table, column) in new[] { ("anime_archives", "TitleSnapshot"), ("anime_plans", "TitleSnapshot"),
            ("browse_history", "TitleSnapshot"), ("manual_watch_events", "TitleSnapshot"),
            ("recommendation_hidden_anime", "TitleSnapshot"), ("screenshots", "AnimeTitle") })
            foreach (var row in input.Rows(table))
                if (MigrationInput.Id(row["AnimeId"]) is not 0 and var id && row.GetValueOrDefault(column) is { Length: > 0 } title)
                    input.Titles.TryAdd(id, title);
        return input;
    }
}
