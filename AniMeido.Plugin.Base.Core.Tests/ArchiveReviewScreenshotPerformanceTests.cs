using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using System.Diagnostics;
using Xunit.Abstractions;

namespace AniMeido.Tests;

public sealed class ArchiveReviewScreenshotPerformanceTests(ITestOutputHelper output) : IAsyncLifetime
{
    private const int ReviewYear = 2025;
    private const int ContextLength = 256;
    private const int WarmupRounds = 3;
    private const int MeasurementRounds = 5;
    private static readonly DateTimeOffset LatestCapture = new(2025, 7, 15, 12, 0, 0, TimeSpan.Zero);
    private readonly PersonalDatabaseFixture _database = new();

    public Task InitializeAsync() => _database.InitializeAsync();
    public Task DisposeAsync() => _database.DisposeAsync();

    public enum ScreenshotAvailability { LatestAvailable, FirstFiftyMissing, AllMissing }

    [Theory]
    [InlineData(1000, ScreenshotAvailability.LatestAvailable)]
    [InlineData(10000, ScreenshotAvailability.LatestAvailable)]
    [InlineData(1000, ScreenshotAvailability.FirstFiftyMissing)]
    [InlineData(10000, ScreenshotAvailability.FirstFiftyMissing)]
    [InlineData(1000, ScreenshotAvailability.AllMissing)]
    [InlineData(10000, ScreenshotAvailability.AllMissing)]
    [Trait("Category", "Performance")]
    public async Task AnnualScreenshotPreview_PreservesVisibleRawRecords_AndReportsQueryCost(
        int count, ScreenshotAvailability availability)
    {
        var root = Path.Combine(Path.GetDirectoryName(_database.Factory.DatabasePath)!, "review-preview-files");
        await SeedAsync(root, count, availability);
        var expected = ExpectedPreview(root, availability);
        var queries = new ArchiveQueryService(new ArchiveService(_database.Factory));
        await AssertStoredRowsAsync(count);
        for (var round = 0; round < WarmupRounds; round++)
        {
            var result = await ReadPreviewAsync(queries);
            AssertPreview(count, expected, result.Review, result.Visible);
            await AssertStoredRowsAsync(count);
        }

        var times = new double[MeasurementRounds];
        var allocations = new long[MeasurementRounds];
        var returnedCounts = new int[MeasurementRounds];
        var visibleCounts = new int[MeasurementRounds];
        for (var round = 0; round < MeasurementRounds; round++)
        {
            var beforeBytes = GC.GetTotalAllocatedBytes(precise: true);
            var started = Stopwatch.GetTimestamp();
            var result = await ReadPreviewAsync(queries);
            times[round] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            allocations[round] = GC.GetTotalAllocatedBytes(precise: true) - beforeBytes;

            returnedCounts[round] = result.Review.Screenshots.Count;
            visibleCounts[round] = result.Visible.Length;
            AssertPreview(count, expected, result.Review, result.Visible);
            await AssertStoredRowsAsync(count);
        }

        output.WriteLine(
            "Screenshots={0}; Availability={1}; Year={2}; ContextChars={3}; Warmups={4}; Measurements={5}",
            count, availability, ReviewYear, ContextLength, WarmupRounds, MeasurementRounds);
        output.WriteLine("LoadReviewThenPagePreview: MedianTotalMs={0:F3}; MedianProcessAllocatedBytes={1}",
            times.Order().ElementAt(MeasurementRounds / 2), allocations.Order().ElementAt(MeasurementRounds / 2));
        output.WriteLine("RawReturnedCounts={0}; VisibleCounts={1}",
            string.Join(",", returnedCounts), string.Join(",", visibleCounts));
        output.WriteLine("Warm synthetic database; setup and assertions excluded. Placeholder files measure File.Exists only, not image decoding. Run this class alone: allocations cover the whole process across async work.");
    }

    private async Task SeedAsync(string root, int count, ScreenshotAvailability availability)
    {
        Directory.CreateDirectory(root);
        using var connection = await _database.Factory.OpenAsync();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // FileExists 是第十四个原始模型值，由占位文件决定，不在数据库里添加对应列。
        command.CommandText = """
            INSERT INTO screenshots(
                ScreenshotId, FilePath, Sha256, CapturedAt, WindowTitle,
                ProcessName, Width, Height, AnimeId, AnimeTitle,
                EpisodeNumber, PlaybackPositionSeconds, ContextNote)
            VALUES(@id, @path, @sha256, @capturedAt, 'fixture-window',
                   'fixture-process', 1920, 1080, 17, '年度截图作品', 3, 42.5, @note);
            """;
        var id = command.Parameters.AddWithValue("@id", string.Empty);
        var path = command.Parameters.AddWithValue("@path", string.Empty);
        var capturedAt = command.Parameters.AddWithValue("@capturedAt", string.Empty);
        command.Parameters.AddWithValue("@sha256", new string('a', 64));
        command.Parameters.AddWithValue("@note", new string('备', ContextLength));
        for (var index = 0; index < count; index++)
        {
            var screenshotId = $"review-{index:D5}";
            var filePath = Path.Combine(root, screenshotId + ".png");
            var exists = availability switch
            {
                ScreenshotAvailability.LatestAvailable => true,
                ScreenshotAvailability.FirstFiftyMissing => index >= 50,
                ScreenshotAvailability.AllMissing => false,
                _ => throw new ArgumentOutOfRangeException(nameof(availability)),
            };
            if (exists)
                await File.WriteAllBytesAsync(filePath, []);
            id.Value = screenshotId;
            path.Value = filePath;
            capturedAt.Value = LatestCapture.AddMinutes(-index).ToString("O");
            await command.ExecuteNonQueryAsync();
        }
        transaction.Commit();
    }

    private static AnimeScreenshot[] ExpectedPreview(string root, ScreenshotAvailability availability)
        => availability switch
        {
            ScreenshotAvailability.LatestAvailable =>
            [
                ExpectedScreenshot(root, "review-00000", new DateTimeOffset(2025, 7, 15, 12, 0, 0, TimeSpan.Zero)),
                ExpectedScreenshot(root, "review-00001", new DateTimeOffset(2025, 7, 15, 11, 59, 0, TimeSpan.Zero)),
            ],
            ScreenshotAvailability.FirstFiftyMissing =>
            [
                ExpectedScreenshot(root, "review-00050", new DateTimeOffset(2025, 7, 15, 11, 10, 0, TimeSpan.Zero)),
                ExpectedScreenshot(root, "review-00051", new DateTimeOffset(2025, 7, 15, 11, 9, 0, TimeSpan.Zero)),
            ],
            ScreenshotAvailability.AllMissing => [],
            _ => throw new ArgumentOutOfRangeException(nameof(availability)),
        };

    private static AnimeScreenshot ExpectedScreenshot(string root, string id, DateTimeOffset capturedAt)
        => new(id, Path.Combine(root, id + ".png"), new string('a', 64), capturedAt,
            "fixture-window", "fixture-process", 1920, 1080, 17, "年度截图作品", 3, 42.5,
            new string('备', ContextLength), true);

    private static async Task<(ArchiveReviewData Review, AnimeScreenshot[] Visible)> ReadPreviewAsync(
        ArchiveQueryService queries)
    {
        var review = await queries.LoadReviewAsync(ReviewYear);
        var visible = review.Screenshots
            .Where(item => item.FileExists && item.CapturedAt.ToLocalTime().Year == ReviewYear)
            .Take(2)
            .ToArray();
        return (review, visible);
    }

    private static void AssertPreview(int count, AnimeScreenshot[] expected,
        ArchiveReviewData review, AnimeScreenshot[] visible)
    {
        Assert.Equal(ReviewYear, review.Year);
        Assert.Equal(count, review.Statistics.ScreenshotCount);
        Assert.Equal(expected.Select(item => item.ScreenshotId), visible.Select(item => item.ScreenshotId));
        Assert.Equal(expected, visible);
    }

    private async Task AssertStoredRowsAsync(int count)
    {
        using var connection = await _database.Factory.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*), MIN(LENGTH(ContextNote)), MAX(LENGTH(ContextNote)), SUM(LENGTH(ContextNote))
            FROM screenshots;
            """;
        using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(count, reader.GetInt32(0));
        Assert.Equal(ContextLength, reader.GetInt32(1));
        Assert.Equal(ContextLength, reader.GetInt32(2));
        Assert.Equal((long)count * ContextLength, reader.GetInt64(3));
    }
}
