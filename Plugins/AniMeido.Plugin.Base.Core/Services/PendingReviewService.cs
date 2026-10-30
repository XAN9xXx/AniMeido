using AniMeido.Plugin.Base.Models;

namespace AniMeido.Plugin.Base.Services;

public sealed class PendingReviewService(SqliteConnectionFactory db, PersonalIdRemapService remap)
{
    public Task<IReadOnlyList<PendingReviewItem>> GetItemsAsync(CancellationToken ct = default)
        => Task.Run(() => ReadItemsAsync(ct), ct);

    private async Task<IReadOnlyList<PendingReviewItem>> ReadItemsAsync(CancellationToken ct)
    {
        using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT LocalId,LegacyBangumiId,Reason,NULL,TitleSnapshot,KeptAt FROM legacy_anime_refs
            UNION ALL SELECT AnimeId,NULL,Kind,MergedInto,TitleSnapshot,KeptAt FROM public_id_issues
            ORDER BY 1
            """;
        var result = new List<PendingReviewItem>();
        var missingTitles = new HashSet<int>();
        using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var id = reader.GetInt32(0);
                if (reader.IsDBNull(4))
                    missingTitles.Add(id);
                result.Add(new PendingReviewItem(
                    id,
                    reader.IsDBNull(1) ? null : reader.GetInt32(1),
                    reader.IsDBNull(4) ? AnimeTitleFallback.Untitled : reader.GetString(4),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetInt32(3),
                    MergedIntoTitle: null,
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    new Dictionary<string, int>()));
            }
        }

        if (result.Count == 0)
            return result;

        var titles = await PersonalRecordReader.ReadTitlesAsync(connection, transaction, ct).ConfigureAwait(false);
        var counts = await PersonalRecordReader.ReadPendingCountsAsync(connection, transaction, ct).ConfigureAwait(false);
        return result.Select(item => item with
        {
            Title = missingTitles.Contains(item.AnimeId)
                ? titles.GetValueOrDefault(item.AnimeId) ?? AnimeTitleFallback.Untitled
                : item.Title,
            MergedIntoTitle = item.MergedInto is { } target ? titles.GetValueOrDefault(target) : null,
            RecordCounts = counts.GetValueOrDefault(item.AnimeId) ?? item.RecordCounts,
        }).ToList();
    }

    public Task<IReadOnlyList<LocalAnimeTarget>> GetLocalTargetsAsync(CancellationToken ct = default)
        => Task.Run(() => ReadLocalTargetsAsync(ct), ct);

    private async Task<IReadOnlyList<LocalAnimeTarget>> ReadLocalTargetsAsync(CancellationToken ct)
    {
        using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();
        var ids = await PersonalRecordReader.ReadAnimeIdsAsync(connection, transaction, ct).ConfigureAwait(false);
        var titles = await PersonalRecordReader.ReadTitlesAsync(connection, transaction, ct).ConfigureAwait(false);
        return ids.Select(id => new LocalAnimeTarget(id, titles.GetValueOrDefault(id) ?? AnimeTitleFallback.Untitled))
            .OrderBy(target => target.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public Task SetKeptAsync(int id, bool kept, CancellationToken ct = default)
        => remap.CommitIdentityChangesAsync(async (connection, transaction, _) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = id < 0
                ? "UPDATE legacy_anime_refs SET KeptAt=@at WHERE LocalId=@id"
                : "UPDATE public_id_issues SET KeptAt=@at WHERE AnimeId=@id";
            command.Parameters.AddWithValue("@id", id);
            command.Parameters.AddWithValue("@at", kept ? DateTimeOffset.UtcNow.ToString("O") : (object)DBNull.Value);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, ct);
}
