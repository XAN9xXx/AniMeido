using AniMeido.Plugin.Base.Models;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text.Json;

namespace AniMeido.Plugin.Base.Services;

/// <summary>截图元数据的存储与完整写操作；截图文件的保存、回收和导入恢复由外层业务负责。</summary>
internal sealed class ArchiveScreenshotStore
{
    private const string ScreenshotSettingsKey = "screenshot_settings_v1";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
    private readonly SqliteConnectionFactory _dbFactory;

    internal ArchiveScreenshotStore(SqliteConnectionFactory dbFactory)
        => _dbFactory = dbFactory;

    internal async Task<IReadOnlyList<AnimeScreenshot>> GetScreenshotsAsync(
        int? animeId = null,
        CancellationToken cancellationToken = default,
        int? year = null)
    {
        await using var connection = await _dbFactory.OpenAsync(cancellationToken);
        return await ReadScreenshotsAsync(connection, null, animeId, year, cancellationToken);
    }

    internal async Task<IReadOnlyList<AnimeScreenshot>> GetReviewScreenshotsAsync(
        int year,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dbFactory.OpenAsync(cancellationToken);
        return await ReadScreenshotsCoreAsync(connection, null, null, year, cancellationToken, reviewPreview: true);
    }

    internal static Task<IReadOnlyList<AnimeScreenshot>> ReadScreenshotsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        int? animeId,
        int? year,
        CancellationToken cancellationToken)
        => ReadScreenshotsCoreAsync(connection, transaction, animeId, year, cancellationToken, reviewPreview: false);

    private static async Task<IReadOnlyList<AnimeScreenshot>> ReadScreenshotsCoreAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        int? animeId,
        int? year,
        CancellationToken cancellationToken,
        bool reviewPreview)
    {
        var bounds = year is null ? (Start: (string?)null, End: (string?)null)
            : LocalYearRangeUtc(year.Value);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var filter = (animeId is not null, year is not null) switch
        {
            (true, true) => "WHERE AnimeId = @animeId AND CapturedAt >= @start AND CapturedAt < @end",
            (true, false) => "WHERE AnimeId = @animeId",
            (false, true) => "WHERE CapturedAt >= @start AND CapturedAt < @end",
            _ => string.Empty,
        };
        command.CommandText = $"""
            SELECT ScreenshotId, FilePath, Sha256, CapturedAt, WindowTitle,
                   ProcessName, Width, Height, AnimeId, AnimeTitle,
                   EpisodeNumber, PlaybackPositionSeconds, ContextNote
            FROM screenshots
            {filter}
            ORDER BY CapturedAt DESC
            """;
        command.Parameters.AddWithValue(
            "@animeId",
            animeId is null ? DBNull.Value : animeId.Value);
        command.Parameters.AddWithValue(
            "@start",
            bounds.Start is null ? DBNull.Value : bounds.Start);
        command.Parameters.AddWithValue(
            "@end",
            bounds.End is null ? DBNull.Value : bounds.End);
        var items = new List<AnimeScreenshot>();
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var path = reader.GetString(1);
            DateTimeOffset? capturedAt = null;
            bool? fileExists = null;
            if (reviewPreview)
            {
                capturedAt = ParseTimestamp(reader.GetString(3));
                if (capturedAt.Value.ToLocalTime().Year != year)
                    continue;
                fileExists = File.Exists(path);
                if (!fileExists.Value)
                    continue;
            }

            items.Add(ReadScreenshot(reader, path, capturedAt, fileExists));
            if (reviewPreview && items.Count == 2)
                break;
        }

        return items;
    }

    // 预览复用筛选时的读值；完整读取仍在字段映射后探测文件，不改变原异常顺序。
    private static AnimeScreenshot ReadScreenshot(
        SqliteDataReader reader,
        string path,
        DateTimeOffset? capturedAt,
        bool? fileExists)
        => new(
            reader.GetString(0),
            path,
            reader.GetString(2),
            capturedAt ?? ParseTimestamp(reader.GetString(3)),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetInt32(6),
            reader.GetInt32(7),
            reader.IsDBNull(8) ? null : reader.GetInt32(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetInt32(10),
            reader.IsDBNull(11) ? null : reader.GetDouble(11),
            reader.GetString(12),
            fileExists ?? File.Exists(path));

    internal async Task InsertScreenshotAsync(
        AnimeScreenshot item,
        CancellationToken cancellationToken = default)
    {
        using var operation = _dbFactory.Access.BeginWrite();
        await Task.Run(async () =>
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
        }).ConfigureAwait(false);
    }

    internal async Task ImportScreenshotsAsync(
        IReadOnlyList<AnimeScreenshot> items,
        CancellationToken cancellationToken = default)
    {
        using var operation = _dbFactory.Access.BeginWrite();
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

    internal async Task DeleteScreenshotRecordAsync(
        string screenshotId,
        CancellationToken cancellationToken = default)
    {
        using var operation = _dbFactory.Access.BeginWrite();
        await Task.Run(async () =>
        {
            await using var connection = await _dbFactory.OpenAsync(
                cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText =
                "DELETE FROM screenshots WHERE ScreenshotId = @id";
            command.Parameters.AddWithValue("@id", screenshotId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }).ConfigureAwait(false);
    }

    internal async Task UpdateScreenshotMetadataAsync(
        string screenshotId,
        int? animeId,
        string? animeTitle,
        int? episodeNumber,
        string contextNote,
        CancellationToken cancellationToken = default)
    {
        using var operation = _dbFactory.Access.BeginWrite();
        await Task.Run(async () =>
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
        }).ConfigureAwait(false);
    }

    internal async Task AddScreenshotTagsAsync(
        IReadOnlyList<string> screenshotIds,
        IEnumerable<string> tags,
        CancellationToken cancellationToken = default)
    {
        using var operation = _dbFactory.Access.BeginWrite();
        var normalizedTags = tags
            .Select(tag => tag.Trim())
            .Where(tag => tag.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (screenshotIds.Count == 0 || normalizedTags.Length == 0)
        {
            return;
        }

        await Task.Run(async () =>
        {
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
        }).ConfigureAwait(false);
    }

    internal async Task RemoveScreenshotTagAsync(
        string screenshotId,
        string tag,
        CancellationToken cancellationToken = default)
    {
        using var operation = _dbFactory.Access.BeginWrite();
        await Task.Run(async () =>
        {
            await using var connection = await _dbFactory.OpenAsync(
                cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM screenshot_personal_tags
                WHERE ScreenshotId = @screenshotId
                  AND TagId = (SELECT TagId FROM personal_tags
                               WHERE Name = @tag COLLATE NOCASE)
                """;
            command.Parameters.AddWithValue("@screenshotId", screenshotId);
            command.Parameters.AddWithValue("@tag", tag);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }).ConfigureAwait(false);
    }

    internal Task<IReadOnlyList<string>> GetScreenshotTagsAsync(
        string screenshotId,
        CancellationToken cancellationToken = default)
        => Task.Run<IReadOnlyList<string>>(async () =>
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
        }, cancellationToken);

    internal async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>>
        GetAllScreenshotTagsAsync(
            CancellationToken cancellationToken = default)
    {
        await using var connection = await _dbFactory.OpenAsync(
            cancellationToken);
        return await ReadScreenshotTagsAsync(connection, null, cancellationToken);
    }

    internal static async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> ReadScreenshotTagsAsync(
        SqliteConnection connection, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
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

    internal async Task<int> RemoveMissingScreenshotRecordsAsync(
        CancellationToken cancellationToken = default)
    {
        using var operation = _dbFactory.Access.BeginWrite();
        return await Task.Run(async () =>
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
        }).ConfigureAwait(false);
    }

    internal Task<ScreenshotSettings> GetScreenshotSettingsAsync(
        CancellationToken cancellationToken = default)
        => Task.Run<ScreenshotSettings>(async () =>
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
        }, cancellationToken);

    internal async Task SaveScreenshotSettingsAsync(
        ScreenshotSettings settings,
        CancellationToken cancellationToken = default)
    {
        using var operation = _dbFactory.Access.BeginWrite();
        ArgumentNullException.ThrowIfNull(settings);
        await Task.Run(async () =>
        {
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
        }).ConfigureAwait(false);
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

    private static DateTimeOffset ParseTimestamp(string value)
        => DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
}
