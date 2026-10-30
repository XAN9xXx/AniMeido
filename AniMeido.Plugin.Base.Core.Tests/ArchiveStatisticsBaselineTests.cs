using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using System.Diagnostics;
using System.Globalization;
using Xunit.Abstractions;

namespace AniMeido.Tests;

public sealed partial class ArchiveStatisticsBaselineTests(ITestOutputHelper output) : IAsyncLifetime
{
    private const int ReviewYear = 2025;
    private const int EmptyYear = 2030;
    private static readonly DateTimeOffset OldestRecording = new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly PersonalDatabaseFixture _database = new();
    private SqliteConnectionFactory Factory => _database.Factory;

    public Task InitializeAsync() => _database.InitializeAsync();

    public Task DisposeAsync() => _database.DisposeAsync();

    [Theory]
    [InlineData(1000, 1)]
    [InlineData(1000, 50)]
    [InlineData(10000, 1)]
    [InlineData(10000, 50)]
    [Trait("Category", "Performance")]
    public async Task ProductionStatistics_PreserveAllFieldsAcrossAnnualEmptyAndAllTimeScopes(int count, int annualPercent)
    {
        var annualCount = count * annualPercent / 100;
        await SeedAsync(count, annualCount);
        var archive = new ArchiveService(Factory);
        output.WriteLine("Rows per table={0}; review year={1}; annual rows={2} ({3}%); current production schema/indexes only.",
            count, ReviewYear, annualCount, annualPercent);
        output.WriteLine("Six tables; 3 warmups / 5 samples; setup and independent assertions excluded from timing.");
        output.WriteLine("One rated archive was created before the review year and updated inside it; the oldest playback is unfinished.");

        await MeasureScopeAsync(archive, ReviewYear, Expected(annualCount, annualCount / 2 + 1));
        await MeasureScopeAsync(archive, EmptyYear, Expected(0, 0));
        await MeasureScopeAsync(archive, null, Expected(count, count / 2));
        await AssertSourceCountsAsync(count);
        output.WriteLine("Process allocations require isolated class execution; service measurements are not UI acceptance.");
    }

    private static ArchiveStatistics Expected(int includedRows, int ratedRows)
    {
        // 每组已知构成：一半完成播放（每条 2 分钟），每条手工记录 2–4 集，
        // 一半手工记录有 20 分钟。预期由夹具构成直接计算，不再次筛选或聚合生产记录。
        IReadOnlyDictionary<string, int> tags = includedRows == 0
            ? new Dictionary<string, int>()
            : new Dictionary<string, int> { ["All archives"] = includedRows, ["Even archives"] = includedRows / 2 };
        return new ArchiveStatistics(OldestRecording, includedRows, ratedRows, includedRows, includedRows,
            includedRows, includedRows * 7 / 2, includedRows * 11, tags);
    }

    private async Task MeasureScopeAsync(ArchiveService archive, int? year, ArchiveStatistics expected)
    {
        for (var warmup = 0; warmup < 3; warmup++)
        {
            AssertStatistics(expected, await archive.GetStatisticsAsync(year));
        }
        var milliseconds = new double[5];
        var allocated = new long[5];
        for (var sample = 0; sample < milliseconds.Length; sample++)
        {
            var bytes = GC.GetTotalAllocatedBytes(precise: true);
            var started = Stopwatch.GetTimestamp();
            var result = await archive.GetStatisticsAsync(year);
            milliseconds[sample] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            allocated[sample] = GC.GetTotalAllocatedBytes(precise: true) - bytes;
            AssertStatistics(expected, result);
        }
        output.WriteLine("Scope={0}: median {1:F3} ms / {2} B; samples ms=[{3}]; B=[{4}]",
            year?.ToString(CultureInfo.InvariantCulture) ?? "all time", milliseconds.Order().ElementAt(2),
            allocated.Order().ElementAt(2),
            string.Join(", ", milliseconds.Select(value => value.ToString("F3", CultureInfo.InvariantCulture))),
            string.Join(", ", allocated));
        await ReportRepresentativePlansAsync(year);
    }

    private static void AssertStatistics(ArchiveStatistics expected, ArchiveStatistics actual)
    {
        Assert.Equal(expected.RecordingStartedAt, actual.RecordingStartedAt);
        Assert.Equal(expected.ArchiveCount, actual.ArchiveCount);
        Assert.Equal(expected.RatedCount, actual.RatedCount);
        Assert.Equal(expected.EntryCount, actual.EntryCount);
        Assert.Equal(expected.ScreenshotCount, actual.ScreenshotCount);
        Assert.Equal(expected.TrackingChangeCount, actual.TrackingChangeCount);
        Assert.Equal(expected.CompletedEpisodeCount, actual.CompletedEpisodeCount);
        Assert.Equal(expected.EstimatedWatchMinutes, actual.EstimatedWatchMinutes);
        Assert.Equal(expected.TagCounts.OrderBy(pair => pair.Key, StringComparer.Ordinal),
            actual.TagCounts.OrderBy(pair => pair.Key, StringComparer.Ordinal));
    }

    private async Task SeedAsync(int count, int annualCount)
    {
        var start = LocalYearStart(ReviewYear);
        var end = LocalYearStart(ReviewYear + 1);
        using var connection = await Factory.OpenAsync();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO personal_tags(TagId, Name) VALUES(1, 'All archives'), (2, 'Even archives')";
        await command.ExecuteNonQueryAsync();
        command.CommandText = """
            INSERT INTO anime_archives(AnimeId, TitleSnapshot, PersonalRating, SummaryNote, CreatedAt, UpdatedAt)
            VALUES(@id, 'Archive ' || @id, @rating, @body, @at, @updatedAt);
            INSERT INTO archive_entries(EntryId, AnimeId, OccurredAt, EpisodeNumber, Body, CreatedAt, UpdatedAt)
            VALUES('entry-' || @id, @id, @at, 3, @body, @at, @at);
            INSERT INTO screenshots(ScreenshotId, FilePath, Sha256, CapturedAt, WindowTitle, ProcessName, Width, Height, AnimeId)
            VALUES('shot-' || @id, @path, 'stored-fixture-value', @at, 'Window', 'Process', 640, 360, @id);
            INSERT INTO tracking_events(EventId, AnimeId, PreviousStatus, NewStatus, ChangedAt)
            VALUES('tracking-' || @id, @id, NULL, 2, @at);
            INSERT INTO watch_sessions(EventId, AnimeId, EpisodeNumber, PositionSeconds, DurationSeconds, IsCompleted, ObservedAt)
            VALUES('watch-' || @id, @id, 3, 120, @playerSeconds, @completed, @observedAt);
            INSERT INTO manual_watch_events(EventId, AnimeId, TitleSnapshot, OccurredAt, EpisodeFrom, EpisodeTo, DurationMinutes, Note, CreatedAt)
            VALUES('manual-' || @id, @id, 'Manual ' || @id, @at, 2, 4, @manualMinutes, @body, @at);
            INSERT INTO anime_personal_tags(AnimeId, TagId) VALUES(@id, 1);
            INSERT INTO anime_personal_tags(AnimeId, TagId) SELECT @id, 2 WHERE @completed = 1;
            """;
        var id = command.Parameters.AddWithValue("@id", 0);
        var rating = command.Parameters.AddWithValue("@rating", DBNull.Value);
        var at = command.Parameters.AddWithValue("@at", "");
        var updatedAt = command.Parameters.AddWithValue("@updatedAt", "");
        var observedAt = command.Parameters.AddWithValue("@observedAt", "");
        var completed = command.Parameters.AddWithValue("@completed", 0);
        var playerSeconds = command.Parameters.AddWithValue("@playerSeconds", 0);
        var manualMinutes = command.Parameters.AddWithValue("@manualMinutes", DBNull.Value);
        command.Parameters.AddWithValue("@body", new string('文', 512));
        command.Parameters.AddWithValue("@path", Path.Combine(Path.GetDirectoryName(Factory.DatabasePath)!, "missing.png"));
        for (var index = 0; index < count; index++)
        {
            // 恰好 annualCount 条在目标年内，并显式覆盖 [start, end) 的四个边界点。
            var timestamp = index switch
            {
                0 => start.AddTicks(-1),
                1 => start,
                2 => end.AddTicks(-1),
                3 => end,
                _ when index < annualCount + 2 => start.AddDays(30).AddMinutes(index),
                _ => start.AddYears(-1).AddMinutes(index),
            };
            var isCompleted = index % 2 == 0;
            id.Value = index + 1;
            rating.Value = isCompleted ? 8.5 : DBNull.Value;
            at.Value = timestamp.ToString("O");
            updatedAt.Value = (index == 0 ? start.AddDays(60) : timestamp).ToString("O");
            observedAt.Value = (index == count - 1 ? OldestRecording : timestamp).ToString("O");
            completed.Value = isCompleted ? 1 : 0;
            playerSeconds.Value = isCompleted ? 120 : 999;
            manualMinutes.Value = isCompleted ? 20 : DBNull.Value;
            await command.ExecuteNonQueryAsync();
        }
        transaction.Commit();
    }

    private async Task AssertSourceCountsAsync(int count)
    {
        using var connection = await Factory.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT (SELECT COUNT(*) FROM anime_archives), (SELECT COUNT(*) FROM archive_entries),
                   (SELECT COUNT(*) FROM screenshots), (SELECT COUNT(*) FROM tracking_events),
                   (SELECT COUNT(*) FROM watch_sessions), (SELECT COUNT(*) FROM manual_watch_events)
            """;
        using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        for (var column = 0; column < 6; column++)
        {
            Assert.Equal(count, reader.GetInt32(column));
        }
    }

    private async Task ReportRepresentativePlansAsync(int? year)
    {
        // 原样摘取生产汇总 SQL 的代表子项和标签查询；不冒充整条汇总语句的执行计划。
        var queries = new (string Name, string Sql)[]
        {
            ("Entry count subquery", """
                SELECT COUNT(*) FROM archive_entries
                WHERE (@start IS NULL OR (OccurredAt >= @start AND OccurredAt < @end))
                """),
            ("Completed playback minutes subquery", """
                SELECT COALESCE(SUM(DurationSeconds) / 60, 0) FROM watch_sessions
                WHERE IsCompleted = 1 AND (@start IS NULL OR (ObservedAt >= @start AND ObservedAt < @end))
                """),
            ("All-history earliest subquery", """
                SELECT MIN(Value) FROM (
                    SELECT MIN(CreatedAt) Value FROM anime_archives
                    UNION ALL SELECT MIN(OccurredAt) FROM archive_entries
                    UNION ALL SELECT MIN(ObservedAt) FROM watch_sessions
                    UNION ALL SELECT MIN(OccurredAt) FROM manual_watch_events
                    UNION ALL SELECT MIN(CapturedAt) FROM screenshots
                    UNION ALL SELECT MIN(ChangedAt) FROM tracking_events
                )
                """),
            ("Tag counts query", """
                SELECT t.Name, COUNT(*)
                FROM personal_tags t
                JOIN anime_personal_tags at ON at.TagId = t.TagId
                JOIN anime_archives a ON a.AnimeId = at.AnimeId
                WHERE @start IS NULL OR (a.CreatedAt >= @start AND a.CreatedAt < @end)
                GROUP BY t.TagId, t.Name
                ORDER BY COUNT(*) DESC, t.Name COLLATE NOCASE
                """),
        };
        using var connection = await Factory.OpenAsync();
        foreach (var query in queries)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "EXPLAIN QUERY PLAN " + query.Sql;
            command.Parameters.AddWithValue("@start", year is null ? DBNull.Value : LocalYearStart(year.Value).ToString("O"));
            command.Parameters.AddWithValue("@end", year is null ? DBNull.Value : LocalYearStart(year.Value + 1).ToString("O"));
            using var reader = await command.ExecuteReaderAsync();
            var steps = new List<string>();
            while (await reader.ReadAsync())
            {
                steps.Add(reader.GetString(3));
            }
            output.WriteLine("  Current plan, {0}: {1}", query.Name, string.Join("; ", steps));
        }
    }

    private static DateTimeOffset LocalYearStart(int year)
        => new DateTimeOffset(new DateTime(year, 1, 1),
            TimeZoneInfo.Local.GetUtcOffset(new DateTime(year, 1, 1))).ToUniversalTime();
}
