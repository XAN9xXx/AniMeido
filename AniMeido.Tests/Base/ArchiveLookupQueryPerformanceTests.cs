using AniMeido.Plugin.Base.Services;
using System.Diagnostics;
using Xunit.Abstractions;

namespace AniMeido.Tests;

public sealed class ArchiveLookupQueryPerformanceTests(ITestOutputHelper output) : DbTestBase
{
    private static readonly DateTimeOffset At = new(2025, 6, 1, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(1000)]
    [InlineData(10000)]
    [Trait("Category", "Performance")]
    public async Task WorkLookup_ReturnsOnlyItsHistoryAndScreenshotsAsUnrelatedTablesGrow(int unrelatedCount)
    {
        await RunProductionMigrationAsync();
        var missingFile = Path.Combine(Path.GetDirectoryName(DbPath)!, "missing.png");
        using (var connection = await DbFactory.OpenAsync())
        using (var transaction = connection.BeginTransaction())
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO watch_sessions(EventId, AnimeId, EpisodeNumber, PositionSeconds, DurationSeconds, IsCompleted, ObservedAt)
                VALUES('other-player-' || @row, @animeId, 1, 120, 1200, 1, @at);
                INSERT INTO manual_watch_events(EventId, AnimeId, TitleSnapshot, OccurredAt, EpisodeFrom, EpisodeTo, DurationMinutes, Note, CreatedAt)
                VALUES('other-manual-' || @row, @animeId, 'Unrelated', @at, 1, 2, 40, @note, @at);
                INSERT INTO screenshots(ScreenshotId, FilePath, Sha256, CapturedAt, WindowTitle, ProcessName, Width, Height, AnimeId, ContextNote)
                VALUES('other-shot-' || @row, @path, 'stored-fixture-value', @at, 'Window', 'Process', 640, 360, @animeId, @note)
                """;
            var row = command.Parameters.AddWithValue("@row", 0);
            var animeId = command.Parameters.AddWithValue("@animeId", 0);
            var at = command.Parameters.AddWithValue("@at", "");
            command.Parameters.AddWithValue("@note", new string('文', 256));
            command.Parameters.AddWithValue("@path", missingFile);
            for (var index = 1; index <= unrelatedCount; index++)
            {
                row.Value = index;
                animeId.Value = 1000 + index;
                at.Value = At.AddSeconds(index).ToString("O");
                await command.ExecuteNonQueryAsync();
            }

            command.CommandText = """
                INSERT INTO anime_archives(AnimeId, TitleSnapshot, CreatedAt, UpdatedAt)
                VALUES(42, 'Selected', @at, @at);
                INSERT INTO watch_sessions VALUES('selected-player-old',42,1,120,150.9,0,@old),
                                                 ('selected-player-new',42,2,900,1200,1,@new);
                INSERT INTO manual_watch_events VALUES('selected-manual-old',42,'Manual Selected',@manualOld,1,2,40,'note',@at),
                                                       ('selected-manual-new',42,'Manual Selected',@manualNew,3,4,40,'note',@at);
                INSERT INTO screenshots(ScreenshotId, FilePath, Sha256, CapturedAt, WindowTitle, ProcessName, Width, Height, AnimeId)
                VALUES('selected-shot-0',@path,'stored-selected-0',@old,'Window','Process',640,360,42),
                      ('selected-shot-1',@path,'stored-selected-1',@manualOld,'Window','Process',640,360,42),
                      ('selected-shot-2',@path,'stored-selected-2',@new,'Window','Process',640,360,42)
                """;
            command.Parameters.AddWithValue("@old", At.ToString("O"));
            command.Parameters.AddWithValue("@manualOld", At.AddSeconds(1).ToString("O"));
            command.Parameters.AddWithValue("@new", At.AddSeconds(2).ToString("O"));
            command.Parameters.AddWithValue("@manualNew", At.AddSeconds(3).ToString("O"));
            await command.ExecuteNonQueryAsync();
            transaction.Commit();
        }

        var archive = new ArchiveService(DbFactory);
        async Task ReadHistoryAsync()
        {
            var rows = await archive.GetWatchHistoryAsync(42);
            Assert.Equal(new[] { "selected-manual-new", "selected-player-new", "selected-manual-old", "selected-player-old" },
                rows.Select(row => row.EventId));
        }
        async Task ReadScreenshotsAsync()
        {
            var rows = await archive.GetScreenshotsAsync(42);
            Assert.Equal(new[] { "selected-shot-2", "selected-shot-1", "selected-shot-0" },
                rows.Select(row => row.ScreenshotId));
            Assert.All(rows, row => Assert.False(row.FileExists));
        }

        var history = await MeasureAsync(ReadHistoryAsync);
        var screenshots = await MeasureAsync(ReadScreenshotsAsync);
        output.WriteLine("{0} unrelated rows per table; selected work keeps 4 history rows and 3 screenshots:", unrelatedCount);
        output.WriteLine("Watch history: median {0:F3} ms, {1:N0} B", history.Milliseconds, history.Bytes);
        output.WriteLine("Screenshots: median {0:F3} ms, {1:N0} B", screenshots.Milliseconds, screenshots.Bytes);
        using var checkConnection = await DbFactory.OpenAsync();
        using var check = checkConnection.CreateCommand();
        check.CommandText = "SELECT (SELECT COUNT(*) FROM watch_sessions), (SELECT COUNT(*) FROM manual_watch_events), (SELECT COUNT(*) FROM screenshots)";
        using var reader = await check.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(unrelatedCount + 2, reader.GetInt32(0));
        Assert.Equal(unrelatedCount + 2, reader.GetInt32(1));
        Assert.Equal(unrelatedCount + 3, reader.GetInt32(2));
    }

    private static async Task<(double Milliseconds, long Bytes)> MeasureAsync(Func<Task> action)
    {
        for (var warmup = 0; warmup < 3; warmup++)
        {
            await action();
        }

        var samples = new List<(double Milliseconds, long Bytes)>();
        for (var sample = 0; sample < 5; sample++)
        {
            var before = GC.GetTotalAllocatedBytes(precise: true);
            var timer = Stopwatch.StartNew();
            await action();
            timer.Stop();
            samples.Add((timer.Elapsed.TotalMilliseconds, GC.GetTotalAllocatedBytes(precise: true) - before));
        }

        return (samples.Select(sample => sample.Milliseconds).Order().ElementAt(2),
            samples.Select(sample => sample.Bytes).Order().ElementAt(2));
    }
}
