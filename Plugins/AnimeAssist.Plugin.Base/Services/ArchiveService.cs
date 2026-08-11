using AniMeido.Plugin.Base.Models;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text.Json;

namespace AniMeido.Plugin.Base.Services;

public sealed class ArchiveService
{
    private const string ScreenshotSettingsKey = "screenshot_settings_v1";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
    private readonly SqliteConnectionFactory _dbFactory;

    public ArchiveService(SqliteConnectionFactory dbFactory)
        => _dbFactory = dbFactory;

    public async Task<AnimeArchive?> GetArchiveAsync(
        int animeId,
        CancellationToken cancellationToken = default)
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
    }

    public async Task UpsertArchiveAsync(
        int animeId,
        string title,
        double? rating,
        string summary,
        CancellationToken cancellationToken = default)
    {
        ValidateRating(rating);
        var now = DateTimeOffset.UtcNow.ToString("O");
        await using var connection = await _dbFactory.OpenAsync(
            cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO anime_archives(
                AnimeId, TitleSnapshot, PersonalRating, SummaryNote,
                CreatedAt, UpdatedAt)
            VALUES(@animeId, @title, @rating, @summary, @now, @now)
            ON CONFLICT(AnimeId) DO UPDATE SET
                TitleSnapshot = excluded.TitleSnapshot,
                PersonalRating = excluded.PersonalRating,
                SummaryNote = excluded.SummaryNote,
                UpdatedAt = excluded.UpdatedAt
            """;
        command.Parameters.AddWithValue("@animeId", animeId);
        command.Parameters.AddWithValue("@title", title.Trim());
        command.Parameters.AddWithValue(
            "@rating",
            rating is null ? DBNull.Value : rating.Value);
        command.Parameters.AddWithValue("@summary", summary.Trim());
        command.Parameters.AddWithValue("@now", now);
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
                    WHERE s.AnimeId = a.AnimeId)
            FROM anime_archives a
            LEFT JOIN tracking t ON t.AnimeId = a.AnimeId
            ORDER BY a.UpdatedAt DESC
            """;
        var rows = new List<(
            AnimeArchive Archive,
            int EntryCount,
            int ScreenshotCount,
            AniMeido.Contracts.Models.AnimeTrackingStatus? Status)>();
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
                            reader.GetInt32(6)));
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
                row.Status))
            .ToList();
    }

    public async Task<IReadOnlyList<string>> GetAnimeTagsAsync(
        int animeId,
        CancellationToken cancellationToken = default)
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
    }

    public async Task SetAnimeTagsAsync(
        int animeId,
        IEnumerable<string> tags,
        CancellationToken cancellationToken = default)
    {
        var normalized = tags
            .Select(tag => tag.Trim())
            .Where(tag => tag.Length is > 0 and <= 50)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(30)
            .ToList();
        await using var connection = await _dbFactory.OpenAsync(
            cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            cancellationToken);
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = (SqliteTransaction)transaction;
            delete.CommandText =
                "DELETE FROM anime_personal_tags WHERE AnimeId = @animeId";
            delete.Parameters.AddWithValue("@animeId", animeId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var tag in normalized)
        {
            await using var insertTag = connection.CreateCommand();
            insertTag.Transaction = (SqliteTransaction)transaction;
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

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task AddEntryAsync(
        int animeId,
        DateTimeOffset occurredAt,
        int? episodeNumber,
        string body,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(body);
        if (episodeNumber is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(episodeNumber));
        }

        var now = DateTimeOffset.UtcNow.ToString("O");
        await using var connection = await _dbFactory.OpenAsync(
            cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO archive_entries(
                EntryId, AnimeId, OccurredAt, EpisodeNumber, Body,
                CreatedAt, UpdatedAt)
            VALUES(@id, @animeId, @occurredAt, @episode, @body, @now, @now)
            """;
        command.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
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

    public async Task UpdateEntryAsync(
        string entryId,
        DateTimeOffset occurredAt,
        int? episodeNumber,
        string body,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new ArgumentException("观看感想不能为空。", nameof(body));
        }

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
    }

    public async Task DeleteEntryAsync(
        string entryId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dbFactory.OpenAsync(
            cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "DELETE FROM archive_entries WHERE EntryId = @id";
        command.Parameters.AddWithValue("@id", entryId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AddManualWatchEventAsync(
        ManualWatchEvent item,
        CancellationToken cancellationToken = default)
    {
        if (item.EpisodeFrom <= 0 || item.EpisodeTo < item.EpisodeFrom)
        {
            throw new ArgumentException("集数范围无效。", nameof(item));
        }

        if (item.DurationMinutes is <= 0)
        {
            throw new ArgumentException("观看时长必须大于零。", nameof(item));
        }

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
    }

    public async Task<IReadOnlyList<WatchHistoryItem>>
        GetWatchHistoryAsync(
            int? animeId = null,
            CancellationToken cancellationToken = default)
    {
        await using var connection = await _dbFactory.OpenAsync(
            cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EventId, AnimeId, Title, OccurredAt, EpisodeFrom,
                   EpisodeTo, EstimatedMinutes, Note, IsManual
            FROM (
                SELECT w.EventId,
                       w.AnimeId,
                       COALESCE(a.TitleSnapshot, 'Bangumi #' || w.AnimeId)
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
                WHERE @animeId IS NULL OR w.AnimeId = @animeId
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
                WHERE @animeId IS NULL OR m.AnimeId = @animeId
            )
            ORDER BY OccurredAt DESC
            """;
        command.Parameters.AddWithValue(
            "@animeId",
            animeId is null ? DBNull.Value : animeId.Value);
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

    public async Task<IReadOnlyList<AnimeScreenshot>> GetScreenshotsAsync(
        int? animeId = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dbFactory.OpenAsync(
            cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ScreenshotId, FilePath, Sha256, CapturedAt, WindowTitle,
                   ProcessName, Width, Height, AnimeId, AnimeTitle,
                   EpisodeNumber, PlaybackPositionSeconds, ContextNote
            FROM screenshots
            WHERE @animeId IS NULL OR AnimeId = @animeId
            ORDER BY CapturedAt DESC
            """;
        command.Parameters.AddWithValue(
            "@animeId",
            animeId is null ? DBNull.Value : animeId.Value);
        var items = new List<AnimeScreenshot>();
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var path = reader.GetString(1);
            items.Add(new AnimeScreenshot(
                reader.GetString(0),
                path,
                reader.GetString(2),
                ParseTimestamp(reader.GetString(3)),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetInt32(6),
                reader.GetInt32(7),
                reader.IsDBNull(8) ? null : reader.GetInt32(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetInt32(10),
                reader.IsDBNull(11) ? null : reader.GetDouble(11),
                reader.GetString(12),
                File.Exists(path)));
        }

        return items;
    }

    public async Task InsertScreenshotAsync(
        AnimeScreenshot item,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dbFactory.OpenAsync(
            cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO screenshots(
                ScreenshotId, FilePath, Sha256, CapturedAt, WindowTitle,
                ProcessName, Width, Height, AnimeId, AnimeTitle,
                EpisodeNumber, PlaybackPositionSeconds, ContextNote)
            VALUES(@id, @path, @hash, @capturedAt, @window, @process,
                   @width, @height, @animeId, @animeTitle, @episode,
                   @position, @context)
            """;
        command.Parameters.AddWithValue("@id", item.ScreenshotId);
        command.Parameters.AddWithValue("@path", item.FilePath);
        command.Parameters.AddWithValue("@hash", item.Sha256);
        command.Parameters.AddWithValue(
            "@capturedAt",
            item.CapturedAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("@window", item.WindowTitle);
        command.Parameters.AddWithValue("@process", item.ProcessName);
        command.Parameters.AddWithValue("@width", item.Width);
        command.Parameters.AddWithValue("@height", item.Height);
        command.Parameters.AddWithValue(
            "@animeId",
            item.AnimeId is null ? DBNull.Value : item.AnimeId.Value);
        command.Parameters.AddWithValue(
            "@animeTitle",
            item.AnimeTitle is null ? DBNull.Value : item.AnimeTitle);
        command.Parameters.AddWithValue(
            "@episode",
            item.EpisodeNumber is null
                ? DBNull.Value
                : item.EpisodeNumber.Value);
        command.Parameters.AddWithValue(
            "@position",
            item.PlaybackPositionSeconds is null
                ? DBNull.Value
                : item.PlaybackPositionSeconds.Value);
        command.Parameters.AddWithValue("@context", item.ContextNote);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ImportScreenshotsAsync(
        IReadOnlyList<AnimeScreenshot> items,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dbFactory.OpenAsync(
            cancellationToken);
        await using var transaction =
            (Microsoft.Data.Sqlite.SqliteTransaction)
            await connection.BeginTransactionAsync(cancellationToken);
        foreach (var item in items)
        {
            await using var existing = connection.CreateCommand();
            existing.Transaction = transaction;
            existing.CommandText = """
                SELECT Sha256, FilePath
                FROM screenshots
                WHERE ScreenshotId = @id
                """;
            existing.Parameters.AddWithValue("@id", item.ScreenshotId);
            string? existingHash = null;
            string? existingPath = null;
            await using (var existingReader =
                await existing.ExecuteReaderAsync(cancellationToken))
            {
                if (await existingReader.ReadAsync(cancellationToken))
                {
                    existingHash = existingReader.GetString(0);
                    existingPath = existingReader.GetString(1);
                }
            }

            if (existingHash is not null)
            {
                if (!string.Equals(
                    existingHash,
                    item.Sha256,
                    StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"截图 {item.ScreenshotId} 与本地同 ID 记录的哈希不同。");
                }

                if (!File.Exists(existingPath!)
                    && File.Exists(item.FilePath))
                {
                    await using var repair = connection.CreateCommand();
                    repair.Transaction = transaction;
                    repair.CommandText = """
                        UPDATE screenshots
                        SET FilePath = @path
                        WHERE ScreenshotId = @id
                        """;
                    repair.Parameters.AddWithValue("@path", item.FilePath);
                    repair.Parameters.AddWithValue("@id", item.ScreenshotId);
                    await repair.ExecuteNonQueryAsync(cancellationToken);
                }

                continue;
            }

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO screenshots(
                    ScreenshotId, FilePath, Sha256, CapturedAt,
                    WindowTitle, ProcessName, Width, Height, AnimeId,
                    AnimeTitle, EpisodeNumber, PlaybackPositionSeconds,
                    ContextNote)
                VALUES(@id, @path, @hash, @capturedAt, @window, @process,
                       @width, @height, @animeId, @animeTitle, @episode,
                       @position, @context)
                """;
            command.Parameters.AddWithValue("@id", item.ScreenshotId);
            command.Parameters.AddWithValue("@path", item.FilePath);
            command.Parameters.AddWithValue("@hash", item.Sha256);
            command.Parameters.AddWithValue(
                "@capturedAt",
                item.CapturedAt.ToUniversalTime().ToString("O"));
            command.Parameters.AddWithValue("@window", item.WindowTitle);
            command.Parameters.AddWithValue("@process", item.ProcessName);
            command.Parameters.AddWithValue("@width", item.Width);
            command.Parameters.AddWithValue("@height", item.Height);
            command.Parameters.AddWithValue(
                "@animeId",
                item.AnimeId is null ? DBNull.Value : item.AnimeId.Value);
            command.Parameters.AddWithValue(
                "@animeTitle",
                item.AnimeTitle is null
                    ? DBNull.Value
                    : item.AnimeTitle);
            command.Parameters.AddWithValue(
                "@episode",
                item.EpisodeNumber is null
                    ? DBNull.Value
                    : item.EpisodeNumber.Value);
            command.Parameters.AddWithValue(
                "@position",
                item.PlaybackPositionSeconds is null
                    ? DBNull.Value
                    : item.PlaybackPositionSeconds.Value);
            command.Parameters.AddWithValue("@context", item.ContextNote);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeleteScreenshotRecordAsync(
        string screenshotId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dbFactory.OpenAsync(
            cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "DELETE FROM screenshots WHERE ScreenshotId = @id";
        command.Parameters.AddWithValue("@id", screenshotId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateScreenshotMetadataAsync(
        string screenshotId,
        int? animeId,
        string? animeTitle,
        int? episodeNumber,
        string contextNote,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dbFactory.OpenAsync(
            cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE screenshots
            SET AnimeId = @animeId,
                AnimeTitle = @animeTitle,
                EpisodeNumber = @episode,
                ContextNote = @context
            WHERE ScreenshotId = @id
            """;
        command.Parameters.AddWithValue("@id", screenshotId);
        command.Parameters.AddWithValue(
            "@animeId",
            animeId is null ? DBNull.Value : animeId.Value);
        command.Parameters.AddWithValue(
            "@animeTitle",
            string.IsNullOrWhiteSpace(animeTitle)
                ? DBNull.Value
                : animeTitle.Trim());
        command.Parameters.AddWithValue(
            "@episode",
            episodeNumber is null ? DBNull.Value : episodeNumber.Value);
        command.Parameters.AddWithValue("@context", contextNote.Trim());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AddScreenshotTagsAsync(
        IReadOnlyList<string> screenshotIds,
        IEnumerable<string> tags,
        CancellationToken cancellationToken = default)
    {
        var normalizedTags = tags
            .Select(tag => tag.Trim())
            .Where(tag => tag.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (screenshotIds.Count == 0 || normalizedTags.Length == 0)
        {
            return;
        }

        await using var connection = await _dbFactory.OpenAsync(
            cancellationToken);
        await using var transaction =
            (Microsoft.Data.Sqlite.SqliteTransaction)
            await connection.BeginTransactionAsync(cancellationToken);
        foreach (var tag in normalizedTags)
        {
            await using var insertTag = connection.CreateCommand();
            insertTag.Transaction = transaction;
            insertTag.CommandText =
                "INSERT OR IGNORE INTO personal_tags(Name) VALUES(@name)";
            insertTag.Parameters.AddWithValue("@name", tag);
            await insertTag.ExecuteNonQueryAsync(cancellationToken);

            foreach (var screenshotId in screenshotIds)
            {
                await using var bind = connection.CreateCommand();
                bind.Transaction = transaction;
                bind.CommandText = """
                    INSERT OR IGNORE INTO screenshot_personal_tags(
                        ScreenshotId, TagId)
                    SELECT @screenshotId, TagId
                    FROM personal_tags
                    WHERE Name = @name COLLATE NOCASE
                    """;
                bind.Parameters.AddWithValue(
                    "@screenshotId",
                    screenshotId);
                bind.Parameters.AddWithValue("@name", tag);
                await bind.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<string>> GetScreenshotTagsAsync(
        string screenshotId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dbFactory.OpenAsync(
            cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.Name
            FROM personal_tags t
            JOIN screenshot_personal_tags st ON st.TagId = t.TagId
            WHERE st.ScreenshotId = @id
            ORDER BY t.Name COLLATE NOCASE
            """;
        command.Parameters.AddWithValue("@id", screenshotId);
        var tags = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            tags.Add(reader.GetString(0));
        }

        return tags;
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>>
        GetAllScreenshotTagsAsync(
            CancellationToken cancellationToken = default)
    {
        await using var connection = await _dbFactory.OpenAsync(
            cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT st.ScreenshotId, t.Name
            FROM screenshot_personal_tags st
            JOIN personal_tags t ON t.TagId = st.TagId
            ORDER BY st.ScreenshotId, t.Name COLLATE NOCASE
            """;
        var mutable = new Dictionary<string, List<string>>(
            StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var screenshotId = reader.GetString(0);
            if (!mutable.TryGetValue(screenshotId, out var tags))
            {
                tags = [];
                mutable.Add(screenshotId, tags);
            }

            tags.Add(reader.GetString(1));
        }

        return mutable.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value,
            StringComparer.Ordinal);
    }

    public async Task<int> RemoveMissingScreenshotRecordsAsync(
        CancellationToken cancellationToken = default)
    {
        var screenshots = await GetScreenshotsAsync(
            cancellationToken: cancellationToken);
        var missing = screenshots
            .Where(item => !item.FileExists)
            .Select(item => item.ScreenshotId)
            .ToArray();
        if (missing.Length == 0)
        {
            return 0;
        }

        await using var connection = await _dbFactory.OpenAsync(
            cancellationToken);
        await using var transaction =
            (Microsoft.Data.Sqlite.SqliteTransaction)
            await connection.BeginTransactionAsync(cancellationToken);
        foreach (var id in missing)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "DELETE FROM screenshots WHERE ScreenshotId = @id";
            command.Parameters.AddWithValue("@id", id);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return missing.Length;
    }

    public async Task<ScreenshotSettings> GetScreenshotSettingsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dbFactory.OpenAsync(
            cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT Value FROM config WHERE Key = @key";
        command.Parameters.AddWithValue("@key", ScreenshotSettingsKey);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is string json)
        {
            try
            {
                return JsonSerializer.Deserialize<ScreenshotSettings>(
                    json,
                    JsonOptions) ?? ScreenshotSettings.CreateDefault();
            }
            catch (JsonException)
            {
            }
        }

        return ScreenshotSettings.CreateDefault();
    }

    public async Task SaveScreenshotSettingsAsync(
        ScreenshotSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await using var connection = await _dbFactory.OpenAsync(
            cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO config(Key, Value) VALUES(@key, @value)
            """;
        command.Parameters.AddWithValue("@key", ScreenshotSettingsKey);
        command.Parameters.AddWithValue(
            "@value",
            JsonSerializer.Serialize(settings, JsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ArchiveStatistics> GetStatisticsAsync(
        int? year = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dbFactory.OpenAsync(
            cancellationToken);
        DateTimeOffset? start = year is null
            ? null
            : new DateTimeOffset(year.Value, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var end = start?.AddYears(1);
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
                (SELECT COUNT(*) FROM watch_sessions
                 WHERE IsCompleted = 1
                   AND {DateFilter("ObservedAt")}),
                (SELECT COALESCE(SUM(EpisodeTo - EpisodeFrom + 1), 0)
                 FROM manual_watch_events
                 WHERE {DateFilter("OccurredAt")}),
                (SELECT COALESCE(SUM(DurationSeconds) / 60, 0)
                 FROM watch_sessions
                 WHERE IsCompleted = 1
                   AND {DateFilter("ObservedAt")}),
                (SELECT COALESCE(SUM(DurationMinutes), 0)
                 FROM manual_watch_events
                 WHERE {DateFilter("OccurredAt")}),
                (SELECT MIN(Value) FROM (
                    SELECT MIN(CreatedAt) Value FROM anime_archives
                    UNION ALL SELECT MIN(OccurredAt) FROM archive_entries
                    UNION ALL SELECT MIN(ObservedAt) FROM watch_sessions
                    UNION ALL SELECT MIN(OccurredAt) FROM manual_watch_events
                    UNION ALL SELECT MIN(CapturedAt) FROM screenshots
                    UNION ALL SELECT MIN(ChangedAt) FROM tracking_events
                ))
            """;
        summaryCommand.Parameters.AddWithValue(
            "@start",
            start is null ? DBNull.Value : start.Value.ToString("O"));
        summaryCommand.Parameters.AddWithValue(
            "@end",
            end is null ? DBNull.Value : end.Value.ToString("O"));
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
            start is null ? DBNull.Value : start.Value.ToString("O"));
        tagCommand.Parameters.AddWithValue(
            "@end",
            end is null ? DBNull.Value : end.Value.ToString("O"));
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

    internal static void ValidateRating(double? rating)
    {
        if (rating is null)
        {
            return;
        }

        if (rating is < 0.5 or > 10
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
