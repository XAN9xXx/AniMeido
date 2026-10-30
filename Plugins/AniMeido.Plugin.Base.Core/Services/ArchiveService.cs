using AniMeido.Plugin.Base.Models;
using Microsoft.Data.Sqlite;
using System.Globalization;

namespace AniMeido.Plugin.Base.Services;

public sealed class ArchiveService
{
    private readonly SqliteConnectionFactory _dbFactory;
    private readonly ArchiveScreenshotStore _screenshots;
    internal PersonalDataAccess Access => _dbFactory.Access;

    public ArchiveService(SqliteConnectionFactory dbFactory)
    {
        _dbFactory = dbFactory;
        _screenshots = new ArchiveScreenshotStore(dbFactory);
    }

    public Task<AnimeArchive?> GetArchiveAsync(
        int animeId,
        CancellationToken cancellationToken = default)
        => Task.Run<AnimeArchive?>(async () =>
        {
            await using var connection = await _dbFactory.OpenAsync(
                cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT AnimeId, TitleSnapshot, PersonalRating, SummaryNote,
                       CreatedAt, UpdatedAt
                FROM anime_archives
                WHERE AnimeId = @animeId
                """;
            command.Parameters.AddWithValue("@animeId", animeId);
            await using var reader = await command.ExecuteReaderAsync(
                cancellationToken);
            return await reader.ReadAsync(cancellationToken)
                ? ReadArchive(reader)
                : null;
        }, cancellationToken);

    public async Task SaveArchiveWithTagsAsync(
        int animeId,
        string title,
        double? rating,
        string summary,
        IEnumerable<string> tags,
        CancellationToken cancellationToken = default)
    {
        using var operation = _dbFactory.Access.BeginWrite();
        ValidateRating(rating);
        var normalized = NormalizeTags(tags);
        var now = DateTimeOffset.UtcNow.ToString("O");
        await Task.Run(async () =>
        {
            await using var connection = await _dbFactory.OpenAsync(
                cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(
                cancellationToken);
            await UpsertArchiveInTransactionAsync(
                connection,
                (SqliteTransaction)transaction,
                animeId,
                title,
                rating,
                summary,
                now,
                preserveRating: false,
                preserveSummary: false,
                cancellationToken: cancellationToken);
            await SetAnimeTagsInTransactionAsync(
                connection,
                (SqliteTransaction)transaction,
                animeId,
                normalized,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }).ConfigureAwait(false);
    }

    public async Task UpsertArchiveAsync(
        int animeId,
        string title,
        double? rating,
        string summary,
        CancellationToken cancellationToken = default)
    {
        using var operation = _dbFactory.Access.BeginWrite();
        ValidateRating(rating);
        var now = DateTimeOffset.UtcNow.ToString("O");
        await Task.Run(async () =>
        {
            await using var connection = await _dbFactory.OpenAsync(
                cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(
                cancellationToken);
            await UpsertArchiveInTransactionAsync(
                connection,
                (SqliteTransaction)transaction,
                animeId,
                title,
                rating,
                summary,
                now,
                preserveRating: false,
                preserveSummary: false,
                cancellationToken: cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes an archive row using a caller-owned transaction. Imports can
    /// update the title/summary without overwriting the user's rating.
    /// </summary>
    internal static async Task UpsertArchiveInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int animeId,
        string title,
        double? rating,
        string summary,
        string now,
        bool preserveRating,
        bool preserveSummary,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO anime_archives(
                AnimeId, TitleSnapshot, PersonalRating, SummaryNote,
                CreatedAt, UpdatedAt)
            VALUES(@animeId, @title, @rating, @summary, @now, @now)
            ON CONFLICT(AnimeId) DO UPDATE SET
                TitleSnapshot = excluded.TitleSnapshot,
                PersonalRating = CASE
                    WHEN @preserveRating = 1
                    THEN anime_archives.PersonalRating
                    ELSE excluded.PersonalRating
                END,
                SummaryNote = CASE
                    WHEN @preserveSummary = 1
                    THEN anime_archives.SummaryNote
                    ELSE excluded.SummaryNote
                END,
                UpdatedAt = excluded.UpdatedAt
            """;
        command.Parameters.AddWithValue("@animeId", animeId);
        command.Parameters.AddWithValue("@title", title.Trim());
        command.Parameters.AddWithValue(
            "@rating",
            rating is null ? DBNull.Value : rating.Value);
        command.Parameters.AddWithValue("@summary", summary.Trim());
        command.Parameters.AddWithValue("@now", now);
        command.Parameters.AddWithValue(
            "@preserveRating",
            preserveRating ? 1 : 0);
        command.Parameters.AddWithValue(
            "@preserveSummary",
            preserveSummary ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ArchiveListItem>> GetArchiveListAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dbFactory.OpenAsync(
            cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.AnimeId, a.TitleSnapshot, a.PersonalRating,
                   a.SummaryNote, a.CreatedAt, a.UpdatedAt,
                   t.Status,
                   (SELECT COUNT(*) FROM archive_entries e
                    WHERE e.AnimeId = a.AnimeId),
                   (SELECT COUNT(*) FROM screenshots s
                    WHERE s.AnimeId = a.AnimeId),
                   EXISTS(SELECT 1 FROM public_id_issues i WHERE i.AnimeId=a.AnimeId AND i.Kind='deactivated')
            FROM anime_archives a
            LEFT JOIN tracking t ON t.AnimeId = a.AnimeId
            ORDER BY a.UpdatedAt DESC
            """;
        var rows = new List<(
            AnimeArchive Archive,
            int EntryCount,
            int ScreenshotCount,
            AniMeido.Contracts.Models.AnimeTrackingStatus? Status, bool Unavailable)>();
        await using (var reader = await command.ExecuteReaderAsync(
            cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((
                    ReadArchive(reader),
                    reader.GetInt32(7),
                    reader.GetInt32(8),
                    reader.IsDBNull(6)
                        ? null
                        : (AniMeido.Contracts.Models.AnimeTrackingStatus)
                            reader.GetInt32(6), reader.GetBoolean(9)));
            }
        }

        var tagsByAnime = new Dictionary<int, List<string>>();
        await using var tagCommand = connection.CreateCommand();
        tagCommand.CommandText = """
            SELECT at.AnimeId, t.Name
            FROM anime_personal_tags at
            JOIN personal_tags t ON t.TagId = at.TagId
            ORDER BY at.AnimeId, t.Name COLLATE NOCASE
            """;
        await using var tagReader = await tagCommand.ExecuteReaderAsync(
            cancellationToken);
        while (await tagReader.ReadAsync(cancellationToken))
        {
            var animeId = tagReader.GetInt32(0);
            if (!tagsByAnime.TryGetValue(animeId, out var tags))
            {
                tags = [];
                tagsByAnime.Add(animeId, tags);
            }

            tags.Add(tagReader.GetString(1));
        }

        return rows.Select(row => new ArchiveListItem(
                row.Archive,
                tagsByAnime.GetValueOrDefault(row.Archive.AnimeId) ?? [],
                row.EntryCount,
                row.ScreenshotCount,
                row.Status, row.Unavailable))
            .ToList();
    }

    // 推荐在完整后台刷新中调用，不读取列表展示所需的正文、标签和计数。
    internal async Task<IReadOnlyList<(int AnimeId, string TitleSnapshot, double PersonalRating)>> GetRatedArchiveSeedsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dbFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT AnimeId, TitleSnapshot, PersonalRating
            FROM anime_archives
            WHERE PersonalRating IS NOT NULL
            ORDER BY UpdatedAt DESC
            """;
        var seeds = new List<(int AnimeId, string TitleSnapshot, double PersonalRating)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            seeds.Add((reader.GetInt32(0), reader.GetString(1), reader.GetDouble(2)));
        }

        return seeds;
    }

    // 今日主题在完整后台读取中调用，更新时间用于保留同评分的种子顺序。
    internal async Task<IReadOnlyList<(
        int AnimeId,
        string TitleSnapshot,
        double PersonalRating,
        DateTimeOffset UpdatedAt)>> GetRatedArchiveThemeSeedsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dbFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT AnimeId, TitleSnapshot, PersonalRating, UpdatedAt
            FROM anime_archives
            WHERE PersonalRating IS NOT NULL
            ORDER BY UpdatedAt DESC
            """;
        var seeds = new List<(
            int AnimeId,
            string TitleSnapshot,
            double PersonalRating,
            DateTimeOffset UpdatedAt)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            seeds.Add((
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetDouble(2),
                ParseTimestamp(reader.GetString(3))));
        }

        return seeds;
    }

    internal async Task<IReadOnlyList<ArchiveReviewCover>> GetReviewCoversAsync(
        int year,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dbFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT AnimeId, TitleSnapshot, CreatedAt
            FROM anime_archives
            ORDER BY UpdatedAt DESC
            """;
        var covers = new List<ArchiveReviewCover>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var createdAt = ParseTimestamp(reader.GetString(2));
            if (createdAt.ToLocalTime().Year == year)
            {
                covers.Add(new ArchiveReviewCover(reader.GetInt32(0), reader.GetString(1), createdAt));
            }
        }

        return covers.OrderByDescending(cover => cover.CreatedAt).Take(3).ToArray();
    }

    /// <summary>所有打过分的作品及个人评分（“我的番剧”列表显示用）。</summary>
    public Task<IReadOnlyDictionary<int, double>> GetPersonalRatingsAsync(
        CancellationToken cancellationToken = default)
        => Task.Run<IReadOnlyDictionary<int, double>>(async () =>
        {
            await using var connection = await _dbFactory.OpenAsync(
                cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT AnimeId, PersonalRating
                FROM anime_archives
                WHERE PersonalRating IS NOT NULL
                """;
            var ratings = new Dictionary<int, double>();
            await using var reader = await command.ExecuteReaderAsync(
                cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                ratings[reader.GetInt32(0)] = reader.GetDouble(1);
            }

            return ratings;
        }, cancellationToken);

    public Task<IReadOnlyList<string>> GetAnimeTagsAsync(
        int animeId,
        CancellationToken cancellationToken = default)
        => Task.Run<IReadOnlyList<string>>(async () =>
        {
            await using var connection = await _dbFactory.OpenAsync(
                cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT t.Name
                FROM personal_tags t
                JOIN anime_personal_tags at ON at.TagId = t.TagId
                WHERE at.AnimeId = @animeId
                ORDER BY t.Name COLLATE NOCASE
                """;
            command.Parameters.AddWithValue("@animeId", animeId);
            var tags = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(
                cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                tags.Add(reader.GetString(0));
            }

            return tags;
        }, cancellationToken);

    public async Task SetAnimeTagsAsync(
        int animeId,
        IEnumerable<string> tags,
        CancellationToken cancellationToken = default)
    {
        using var operation = _dbFactory.Access.BeginWrite();
        var normalized = NormalizeTags(tags);
        await Task.Run(async () =>
        {
            await using var connection = await _dbFactory.OpenAsync(
                cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(
                cancellationToken);
            await SetAnimeTagsInTransactionAsync(
                connection,
                (SqliteTransaction)transaction,
                animeId,
                normalized,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }).ConfigureAwait(false);
    }

    private static List<string> NormalizeTags(IEnumerable<string> tags)
        => tags
            .Select(tag => tag.Trim())
            .Where(tag => tag.Length is > 0 and <= 50)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(30)
            .ToList();

    private static async Task SetAnimeTagsInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int animeId,
        IReadOnlyList<string> normalized,
        CancellationToken cancellationToken)
    {
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText =
                "DELETE FROM anime_personal_tags WHERE AnimeId = @animeId";
            delete.Parameters.AddWithValue("@animeId", animeId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var tag in normalized)
        {
            await using var insertTag = connection.CreateCommand();
            insertTag.Transaction = transaction;
            insertTag.CommandText = """
                INSERT OR IGNORE INTO personal_tags(Name) VALUES(@name);
                INSERT OR IGNORE INTO anime_personal_tags(AnimeId, TagId)
                SELECT @animeId, TagId FROM personal_tags
                WHERE Name = @name COLLATE NOCASE;
                """;
            insertTag.Parameters.AddWithValue("@name", tag);
            insertTag.Parameters.AddWithValue("@animeId", animeId);
            await insertTag.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task AddEntryAsync(
        int animeId,
        DateTimeOffset occurredAt,
        int? episodeNumber,
        string body,
        CancellationToken cancellationToken = default)
    {
        using var operation = _dbFactory.Access.BeginWrite();
        ArgumentException.ThrowIfNullOrWhiteSpace(body);
        if (episodeNumber is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(episodeNumber));
        }

        var now = DateTimeOffset.UtcNow.ToString("O");
        await Task.Run(async () =>
        {
            await using var connection = await _dbFactory.OpenAsync(
                cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(
                cancellationToken);
            using (var check = connection.CreateCommand())
            {
                check.Transaction = (SqliteTransaction)transaction;
                check.CommandText = "SELECT EXISTS(SELECT 1 FROM anime_archives WHERE AnimeId = @animeId)";
                check.Parameters.AddWithValue("@animeId", animeId);
                if (Convert.ToInt32(await check.ExecuteScalarAsync(cancellationToken)) == 0)
                {
                    throw new InvalidOperationException("作品尚未建立档案，无法追加条目。");
                }
            }
            await AddEntryInTransactionAsync(
                connection,
                (SqliteTransaction)transaction,
                Guid.NewGuid().ToString("N"),
                animeId,
                occurredAt,
                episodeNumber,
                body,
                now,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Inserts an archive entry using a caller-owned transaction. It does not
    /// create an archive row; callers that need that behavior must use the
    /// explicit ensure-and-add helper below.
    /// </summary>
    internal static async Task AddEntryInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string entryId,
        int animeId,
        DateTimeOffset occurredAt,
        int? episodeNumber,
        string body,
        string now,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO archive_entries(
                EntryId, AnimeId, OccurredAt, EpisodeNumber, Body,
                CreatedAt, UpdatedAt)
            VALUES(@id, @animeId, @occurredAt, @episode, @body, @now, @now)
            """;
        command.Parameters.AddWithValue("@id", entryId);
        command.Parameters.AddWithValue("@animeId", animeId);
        command.Parameters.AddWithValue(
            "@occurredAt",
            occurredAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue(
            "@episode",
            episodeNumber is null ? DBNull.Value : episodeNumber.Value);
        command.Parameters.AddWithValue("@body", body.Trim());
        command.Parameters.AddWithValue("@now", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Ensures the archive exists and inserts one gateway entry atomically.
    /// Existing personal ratings are retained by design.
    /// </summary>
    internal static async Task EnsureArchiveAndAddEntryInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string entryId,
        int animeId,
        string title,
        DateTimeOffset occurredAt,
        int? episodeNumber,
        string body,
        string now,
        CancellationToken cancellationToken)
    {
        await UpsertArchiveInTransactionAsync(
            connection,
            transaction,
            animeId,
            title,
            rating: null,
            summary: string.Empty,
            now: now,
            preserveRating: true,
            preserveSummary: true,
            cancellationToken: cancellationToken);
        await AddEntryInTransactionAsync(
            connection,
            transaction,
            entryId,
            animeId,
            occurredAt,
            episodeNumber,
            body,
            now,
            cancellationToken);
    }

    public async Task<IReadOnlyList<ArchiveEntry>> GetEntriesAsync(
        int animeId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dbFactory.OpenAsync(
            cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EntryId, AnimeId, OccurredAt, EpisodeNumber, Body,
                   CreatedAt, UpdatedAt
            FROM archive_entries
            WHERE AnimeId = @animeId
            ORDER BY OccurredAt DESC
            """;
        command.Parameters.AddWithValue("@animeId", animeId);
        var entries = new List<ArchiveEntry>();
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(new ArchiveEntry(
                reader.GetString(0),
                reader.GetInt32(1),
                ParseTimestamp(reader.GetString(2)),
                reader.IsDBNull(3) ? null : reader.GetInt32(3),
                reader.GetString(4),
                ParseTimestamp(reader.GetString(5)),
                ParseTimestamp(reader.GetString(6))));
        }

        return entries;
    }

    public async Task<IReadOnlyList<AnnualReviewMoment>> GetReviewMomentsAsync(
        int year,
        int limit = 3,
        CancellationToken cancellationToken = default)
    {
        var (start, end) = LocalYearRangeUtc(year);
        await using var connection = await _dbFactory.OpenAsync(
            cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT e.EntryId, e.AnimeId, e.OccurredAt, e.EpisodeNumber,
                   e.Body, e.CreatedAt, e.UpdatedAt, a.TitleSnapshot
            FROM archive_entries e
            LEFT JOIN anime_archives a ON a.AnimeId = e.AnimeId
            WHERE e.OccurredAt >= @start AND e.OccurredAt < @end
            ORDER BY e.OccurredAt DESC
            LIMIT @limit
            """;
        command.Parameters.AddWithValue("@start", start);
        command.Parameters.AddWithValue("@end", end);
        command.Parameters.AddWithValue("@limit", limit);
        var moments = new List<AnnualReviewMoment>();
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var entry = new ArchiveEntry(
                reader.GetString(0),
                reader.GetInt32(1),
                ParseTimestamp(reader.GetString(2)),
                reader.IsDBNull(3) ? null : reader.GetInt32(3),
                reader.GetString(4),
                ParseTimestamp(reader.GetString(5)),
                ParseTimestamp(reader.GetString(6)));
            moments.Add(new AnnualReviewMoment(
                entry,
                reader.IsDBNull(7) ? AnimeTitleFallback.Untitled : reader.GetString(7)));
        }

        return moments;
    }

    public async Task UpdateEntryAsync(
        string entryId,
        DateTimeOffset occurredAt,
        int? episodeNumber,
        string body,
        CancellationToken cancellationToken = default)
    {
        using var operation = _dbFactory.Access.BeginWrite();
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new ArgumentException("观看感想不能为空。", nameof(body));
        }

        if (episodeNumber is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(episodeNumber));
        }

        await Task.Run(async () =>
        {
            await using var connection = await _dbFactory.OpenAsync(
                cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE archive_entries
                SET OccurredAt = @occurredAt,
                    EpisodeNumber = @episode,
                    Body = @body,
                    UpdatedAt = @updatedAt
                WHERE EntryId = @id
                """;
            command.Parameters.AddWithValue("@id", entryId);
            command.Parameters.AddWithValue(
                "@occurredAt",
                occurredAt.ToUniversalTime().ToString("O"));
            command.Parameters.AddWithValue(
                "@episode",
                episodeNumber is null ? DBNull.Value : episodeNumber.Value);
            command.Parameters.AddWithValue("@body", body.Trim());
            command.Parameters.AddWithValue(
                "@updatedAt",
                DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }).ConfigureAwait(false);
    }

    public async Task DeleteEntryAsync(
        string entryId,
        CancellationToken cancellationToken = default)
    {
        using var operation = _dbFactory.Access.BeginWrite();
        await Task.Run(async () =>
        {
            await using var connection = await _dbFactory.OpenAsync(
                cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText =
                "DELETE FROM archive_entries WHERE EntryId = @id";
            command.Parameters.AddWithValue("@id", entryId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }).ConfigureAwait(false);
    }

    public async Task AddManualWatchEventAsync(
        ManualWatchEvent item,
        CancellationToken cancellationToken = default)
    {
        using var operation = _dbFactory.Access.BeginWrite();
        if (item.EpisodeFrom <= 0 || item.EpisodeTo < item.EpisodeFrom)
        {
            throw new ArgumentException("集数范围无效。", nameof(item));
        }

        if (item.DurationMinutes is <= 0)
        {
            throw new ArgumentException("观看时长必须大于零。", nameof(item));
        }

        await Task.Run(async () =>
        {
            await using var connection = await _dbFactory.OpenAsync(
                cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO manual_watch_events(
                    EventId, AnimeId, TitleSnapshot, OccurredAt, EpisodeFrom,
                    EpisodeTo, DurationMinutes, Note, CreatedAt)
                VALUES(@id, @animeId, @title, @occurredAt, @from, @to,
                       @duration, @note, @createdAt)
                """;
            command.Parameters.AddWithValue("@id", item.EventId);
            command.Parameters.AddWithValue("@animeId", item.AnimeId);
            command.Parameters.AddWithValue("@title", item.TitleSnapshot);
            command.Parameters.AddWithValue(
                "@occurredAt",
                item.OccurredAt.ToUniversalTime().ToString("O"));
            command.Parameters.AddWithValue("@from", item.EpisodeFrom);
            command.Parameters.AddWithValue("@to", item.EpisodeTo);
            command.Parameters.AddWithValue(
                "@duration",
                item.DurationMinutes is null
                    ? DBNull.Value
                    : item.DurationMinutes.Value);
            command.Parameters.AddWithValue("@note", item.Note.Trim());
            command.Parameters.AddWithValue(
                "@createdAt",
                item.CreatedAt.ToUniversalTime().ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<WatchHistoryItem>>
        GetWatchHistoryAsync(
            int? animeId = null,
            CancellationToken cancellationToken = default)
    {
        await using var connection = await _dbFactory.OpenAsync(
            cancellationToken);
        await using var command = connection.CreateCommand();
        var playerFilter = animeId is null ? string.Empty : "WHERE w.AnimeId = @animeId";
        var manualFilter = animeId is null ? string.Empty : "WHERE m.AnimeId = @animeId";
        command.CommandText = $"""
            SELECT EventId, AnimeId, Title, OccurredAt, EpisodeFrom,
                   EpisodeTo, EstimatedMinutes, Note, IsManual
            FROM (
                SELECT w.EventId,
                       w.AnimeId,
                       COALESCE(a.TitleSnapshot, @untitled)
                           AS Title,
                       w.ObservedAt AS OccurredAt,
                       w.EpisodeNumber AS EpisodeFrom,
                       w.EpisodeNumber AS EpisodeTo,
                       CAST(w.DurationSeconds / 60 AS INTEGER)
                           AS EstimatedMinutes,
                       '' AS Note,
                       0 AS IsManual
                FROM watch_sessions w
                LEFT JOIN anime_archives a ON a.AnimeId = w.AnimeId
                {playerFilter}
                UNION ALL
                SELECT m.EventId,
                       m.AnimeId,
                       m.TitleSnapshot,
                       m.OccurredAt,
                       m.EpisodeFrom,
                       m.EpisodeTo,
                       m.DurationMinutes,
                       m.Note,
                       1
                FROM manual_watch_events m
                {manualFilter}
            )
            ORDER BY OccurredAt DESC
            """;
        command.Parameters.AddWithValue(
            "@animeId",
            animeId is null ? DBNull.Value : animeId.Value);
        command.Parameters.AddWithValue("@untitled", AnimeTitleFallback.Untitled);
        var results = new List<WatchHistoryItem>();
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new WatchHistoryItem(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetString(2),
                ParseTimestamp(reader.GetString(3)),
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetInt32(6),
                reader.GetString(7),
                reader.GetInt32(8) == 1));
        }

        return results;
    }

    public async Task<IReadOnlyList<ArchiveTrackingChange>>
        GetTrackingChangesAsync(
            int animeId,
            CancellationToken cancellationToken = default)
    {
        await using var connection = await _dbFactory.OpenAsync(
            cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT NewStatus, ChangedAt
            FROM tracking_events
            WHERE AnimeId = @animeId
            ORDER BY ChangedAt DESC
            """;
        command.Parameters.AddWithValue("@animeId", animeId);
        var changes = new List<ArchiveTrackingChange>();
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!reader.IsDBNull(1)
                && DateTimeOffset.TryParse(
                    reader.GetString(1),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out var changedAt))
            {
                changes.Add(new ArchiveTrackingChange(
                    changedAt,
                    (AniMeido.Contracts.Models.AnimeTrackingStatus)
                        reader.GetInt32(0)));
            }
        }

        return changes;
    }

    internal Task<IReadOnlyList<AnimeScreenshot>> GetReviewScreenshotsAsync(
        int year,
        CancellationToken cancellationToken)
        => _screenshots.GetReviewScreenshotsAsync(year, cancellationToken);

    public Task<IReadOnlyList<AnimeScreenshot>> GetScreenshotsAsync(
        int? animeId = null,
        CancellationToken cancellationToken = default,
        int? year = null)
        => _screenshots.GetScreenshotsAsync(animeId, cancellationToken, year);

    internal static Task<IReadOnlyList<AnimeScreenshot>> ReadScreenshotsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        int? animeId,
        int? year,
        CancellationToken cancellationToken)
        => ArchiveScreenshotStore.ReadScreenshotsAsync(connection, transaction, animeId, year, cancellationToken);

    public Task InsertScreenshotAsync(
        AnimeScreenshot item,
        CancellationToken cancellationToken = default)
        => _screenshots.InsertScreenshotAsync(item, cancellationToken);

    public Task ImportScreenshotsAsync(
        IReadOnlyList<AnimeScreenshot> items,
        CancellationToken cancellationToken = default)
        => _screenshots.ImportScreenshotsAsync(items, cancellationToken);

    public Task DeleteScreenshotRecordAsync(
        string screenshotId,
        CancellationToken cancellationToken = default)
        => _screenshots.DeleteScreenshotRecordAsync(screenshotId, cancellationToken);

    public Task UpdateScreenshotMetadataAsync(
        string screenshotId,
        int? animeId,
        string? animeTitle,
        int? episodeNumber,
        string contextNote,
        CancellationToken cancellationToken = default)
        => _screenshots.UpdateScreenshotMetadataAsync(
            screenshotId, animeId, animeTitle, episodeNumber, contextNote, cancellationToken);

    public Task AddScreenshotTagsAsync(
        IReadOnlyList<string> screenshotIds,
        IEnumerable<string> tags,
        CancellationToken cancellationToken = default)
        => _screenshots.AddScreenshotTagsAsync(screenshotIds, tags, cancellationToken);

    public Task RemoveScreenshotTagAsync(
        string screenshotId,
        string tag,
        CancellationToken cancellationToken = default)
        => _screenshots.RemoveScreenshotTagAsync(screenshotId, tag, cancellationToken);

    public Task<IReadOnlyList<string>> GetScreenshotTagsAsync(
        string screenshotId,
        CancellationToken cancellationToken = default)
        => _screenshots.GetScreenshotTagsAsync(screenshotId, cancellationToken);

    public Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetAllScreenshotTagsAsync(
        CancellationToken cancellationToken = default)
        => _screenshots.GetAllScreenshotTagsAsync(cancellationToken);

    internal static Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> ReadScreenshotTagsAsync(
        SqliteConnection connection, SqliteTransaction? transaction, CancellationToken cancellationToken)
        => ArchiveScreenshotStore.ReadScreenshotTagsAsync(connection, transaction, cancellationToken);

    public Task<int> RemoveMissingScreenshotRecordsAsync(
        CancellationToken cancellationToken = default)
        => _screenshots.RemoveMissingScreenshotRecordsAsync(cancellationToken);

    public Task<ScreenshotSettings> GetScreenshotSettingsAsync(
        CancellationToken cancellationToken = default)
        => _screenshots.GetScreenshotSettingsAsync(cancellationToken);

    public Task SaveScreenshotSettingsAsync(
        ScreenshotSettings settings,
        CancellationToken cancellationToken = default)
        => _screenshots.SaveScreenshotSettingsAsync(settings, cancellationToken);

    public async Task<ArchiveStatistics> GetStatisticsAsync(
        int? year = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dbFactory.OpenAsync(
            cancellationToken);
        var bounds = year is null ? (Start: (string?)null, End: (string?)null)
            : LocalYearRangeUtc(year.Value);
        static string DateFilter(string column) =>
            $"(@start IS NULL OR ({column} >= @start AND {column} < @end))";

        await using var summaryCommand = connection.CreateCommand();
        summaryCommand.CommandText = $"""
            SELECT
                (SELECT COUNT(*) FROM anime_archives
                 WHERE {DateFilter("CreatedAt")}),
                (SELECT COUNT(*) FROM anime_archives
                 WHERE PersonalRating IS NOT NULL
                   AND {DateFilter("UpdatedAt")}),
                (SELECT COUNT(*) FROM archive_entries
                 WHERE {DateFilter("OccurredAt")}),
                (SELECT COUNT(*) FROM screenshots
                 WHERE {DateFilter("CapturedAt")}),
                (SELECT COUNT(*) FROM tracking_events
                 WHERE {DateFilter("ChangedAt")}),
                playback.CompletedCount,
                manual.EpisodeCount,
                playback.WatchMinutes,
                manual.WatchMinutes,
                (SELECT MIN(Value) FROM (
                    SELECT MIN(CreatedAt) Value FROM anime_archives
                    UNION ALL SELECT MIN(OccurredAt) FROM archive_entries
                    UNION ALL SELECT MIN(ObservedAt) FROM watch_sessions
                    UNION ALL SELECT MIN(OccurredAt) FROM manual_watch_events
                    UNION ALL SELECT MIN(CapturedAt) FROM screenshots
                    UNION ALL SELECT MIN(ChangedAt) FROM tracking_events
                ))
            FROM (
                SELECT COUNT(*) AS CompletedCount,
                       COALESCE(SUM(DurationSeconds) / 60, 0) AS WatchMinutes
                FROM watch_sessions
                WHERE IsCompleted = 1 AND {DateFilter("ObservedAt")}
            ) playback
            CROSS JOIN (
                SELECT COALESCE(SUM(EpisodeTo - EpisodeFrom + 1), 0) AS EpisodeCount,
                       COALESCE(SUM(DurationMinutes), 0) AS WatchMinutes
                FROM manual_watch_events
                WHERE {DateFilter("OccurredAt")}
            ) manual
            """;
        summaryCommand.Parameters.AddWithValue(
            "@start",
            bounds.Start is null ? DBNull.Value : bounds.Start);
        summaryCommand.Parameters.AddWithValue(
            "@end",
            bounds.End is null ? DBNull.Value : bounds.End);
        await using var summaryReader = await summaryCommand.ExecuteReaderAsync(
            cancellationToken);
        await summaryReader.ReadAsync(cancellationToken);
        var archiveCount = Convert.ToInt32(
            summaryReader.GetValue(0),
            CultureInfo.InvariantCulture);
        var ratedCount = Convert.ToInt32(
            summaryReader.GetValue(1),
            CultureInfo.InvariantCulture);
        var entryCount = Convert.ToInt32(
            summaryReader.GetValue(2),
            CultureInfo.InvariantCulture);
        var screenshotCount = Convert.ToInt32(
            summaryReader.GetValue(3),
            CultureInfo.InvariantCulture);
        var trackingChangeCount = Convert.ToInt32(
            summaryReader.GetValue(4),
            CultureInfo.InvariantCulture);
        var completedFromPlayer = Convert.ToInt32(
            summaryReader.GetValue(5),
            CultureInfo.InvariantCulture);
        var completedManual = Convert.ToInt32(
            summaryReader.GetValue(6),
            CultureInfo.InvariantCulture);
        var estimatedPlayerMinutes = Convert.ToInt32(
            summaryReader.GetValue(7),
            CultureInfo.InvariantCulture);
        var manualMinutes = Convert.ToInt32(
            summaryReader.GetValue(8),
            CultureInfo.InvariantCulture);
        var first = summaryReader.IsDBNull(9)
            ? null
            : summaryReader.GetValue(9);
        DateTimeOffset? recordingStartedAt = first is string firstText
            && !string.IsNullOrWhiteSpace(firstText)
            ? ParseTimestamp(firstText)
            : null;
        await summaryReader.DisposeAsync();

        var tagCounts = new Dictionary<string, int>(
            StringComparer.OrdinalIgnoreCase);
        await using var tagCommand = connection.CreateCommand();
        tagCommand.CommandText = """
            SELECT t.Name, COUNT(*)
            FROM personal_tags t
            JOIN anime_personal_tags at ON at.TagId = t.TagId
            JOIN anime_archives a ON a.AnimeId = at.AnimeId
            WHERE @start IS NULL
               OR (a.CreatedAt >= @start AND a.CreatedAt < @end)
            GROUP BY t.TagId, t.Name
            ORDER BY COUNT(*) DESC, t.Name COLLATE NOCASE
            """;
        tagCommand.Parameters.AddWithValue(
            "@start",
            bounds.Start is null ? DBNull.Value : bounds.Start);
        tagCommand.Parameters.AddWithValue(
            "@end",
            bounds.End is null ? DBNull.Value : bounds.End);
        await using var tagReader = await tagCommand.ExecuteReaderAsync(
            cancellationToken);
        while (await tagReader.ReadAsync(cancellationToken))
        {
            tagCounts[tagReader.GetString(0)] = tagReader.GetInt32(1);
        }

        return new ArchiveStatistics(
            recordingStartedAt,
            archiveCount,
            ratedCount,
            entryCount,
            screenshotCount,
            trackingChangeCount,
            completedFromPlayer + completedManual,
            estimatedPlayerMinutes + manualMinutes,
            tagCounts);
    }

    private static (string Start, string End) LocalYearRangeUtc(int year)
    {
        static string ToUtc(DateTime localDate) => new DateTimeOffset(
            localDate,
            TimeZoneInfo.Local.GetUtcOffset(localDate))
            .ToUniversalTime().ToString("O");

        return (
            ToUtc(new DateTime(year, 1, 1)),
            ToUtc(new DateTime(year + 1, 1, 1)));
    }

    internal static void ValidateRating(double? rating)
    {
        if (rating is null)
        {
            return;
        }

        if (!double.IsFinite(rating.Value)
            || rating is < 0.5 or > 10
            || Math.Abs(rating.Value * 2 - Math.Round(rating.Value * 2))
                > 0.0001)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rating),
                "个人评分必须是 0.5 到 10.0 之间的半分值。");
        }
    }

    private static AnimeArchive ReadArchive(SqliteDataReader reader)
        => new(
            reader.GetInt32(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetDouble(2),
            reader.GetString(3),
            ParseTimestamp(reader.GetString(4)),
            ParseTimestamp(reader.GetString(5)));

    private static DateTimeOffset ParseTimestamp(string value)
        => DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
}
