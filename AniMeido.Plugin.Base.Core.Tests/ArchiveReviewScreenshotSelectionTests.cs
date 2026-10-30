using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using Microsoft.Data.Sqlite;
using System.Globalization;

namespace AniMeido.Tests;

public sealed class ArchiveReviewScreenshotSelectionTests : IAsyncLifetime
{
    private const int ReviewYear = 2025;
    private static readonly DateTimeOffset At = new(ReviewYear, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly PersonalDatabaseFixture _database = new();
    private SqliteConnectionFactory Factory => _database.Factory;
    private int _seedCount;

    public Task InitializeAsync() => _database.InitializeAsync();
    public Task DisposeAsync() => _database.DisposeAsync();

    [Fact]
    public async Task ReviewPreview_SkipsMoreThanTwoMissingFilesAndKeepsTwoCompleteRows()
    {
        await SeedAsync("missing-newest", At.AddMinutes(6), exists: false);
        await SeedAsync("missing-second", At.AddMinutes(5), exists: false);
        await SeedAsync("missing-third", At.AddMinutes(4), exists: false);
        var first = await SeedAsync("first-visible", At.AddMinutes(3), exists: true);
        var second = await SeedAsync("second-visible", At.AddMinutes(2), exists: true);
        await SeedAsync("third-visible", At.AddMinutes(1), exists: true);

        var preview = await AssertMatchesExistingPresentationAsync(ReviewYear);

        Assert.Equal(new[] { "first-visible", "second-visible" }, preview.Select(item => item.ScreenshotId));
        AssertRawFields(first, preview[0]);
        AssertRawFields(second, preview[1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReviewPreview_ReturnsOnlyAvailableRowsWhenFewerThanTwoExist(bool oneExists)
    {
        await SeedAsync("missing-newest", At.AddMinutes(3), exists: false);
        await SeedAsync("only-candidate", At.AddMinutes(2), exists: oneExists);
        await SeedAsync("missing-oldest", At.AddMinutes(1), exists: false);

        var preview = await AssertMatchesExistingPresentationAsync(ReviewYear);

        if (oneExists)
            Assert.Equal("only-candidate", Assert.Single(preview).ScreenshotId);
        else
            Assert.Empty(preview);
    }

    [Fact]
    public async Task ReviewPreview_KeepsAnnualQueryAndLocalYearChecksForStoredOffsets()
    {
        var start = LocalYearStart(ReviewYear);
        var end = LocalYearStart(ReviewYear + 1);
        await SeedAsync("before-start", start.AddTicks(-1).ToUniversalTime(), exists: true);
        await SeedAsync("at-start", start.ToUniversalTime(), exists: true);
        var last = await SeedAsync("last-tick", end.AddTicks(-1).ToUniversalTime(), exists: true);
        await SeedAsync("at-end", end.ToUniversalTime(), exists: true);
        var middle = await SeedAsync("middle", At.AddTicks(1234).ToOffset(TimeSpan.FromHours(5.5)), exists: true);
        // 旧导入可以保留 offset。字符串落在 SQL 范围内，不等于实际本地年份正确。
        await SeedAsync("previous-local-year", start.AddTicks(-1).ToOffset(TimeSpan.FromHours(14)), exists: true);
        await SeedAsync("next-local-year", end.ToOffset(TimeSpan.FromHours(-14)), exists: true);
        var annualRows = await new ArchiveService(Factory).GetScreenshotsAsync(year: ReviewYear);
        Assert.Contains(annualRows, item => item.ScreenshotId == "previous-local-year");
        Assert.Contains(annualRows, item => item.ScreenshotId == "next-local-year");

        var preview = await AssertMatchesExistingPresentationAsync(ReviewYear);

        Assert.Equal(new[] { "last-tick", "middle" }, preview.Select(item => item.ScreenshotId));
        AssertRawFields(last, preview[0]);
        AssertRawFields(middle, preview[1]);
        Assert.All(preview, item => Assert.Equal(ReviewYear, item.CapturedAt.ToLocalTime().Year));
    }

    [Fact]
    public async Task ReviewPreview_EqualTimesFollowTheExistingReaderWithoutAddingATieSort()
    {
        await SeedAsync("z-first-inserted", At, exists: true);
        await SeedAsync("m-second-inserted", At, exists: true);
        await SeedAsync("a-third-inserted", At, exists: true);
        var annualRows = await new ArchiveService(Factory).GetScreenshotsAsync(year: ReviewYear);
        Assert.Equal(3, annualRows.Count);
        Assert.All(annualRows, item => Assert.Equal(At, item.CapturedAt));

        var preview = await AssertMatchesExistingPresentationAsync(ReviewYear);

        // SQL 没有承诺同刻的 ID 次序；这里只锁定与原 reader 相同的前两项。
        Assert.Equal(annualRows.Take(2).Select(item => item.ScreenshotId),
            preview.Select(item => item.ScreenshotId));
    }

    [Fact]
    public async Task ReviewPreview_PreCancellationDoesNotPreventALaterNormalRead()
    {
        await SeedAsync("visible", At, exists: true);
        var queries = new ArchiveQueryService(new ArchiveService(Factory));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queries.LoadReviewAsync(ReviewYear, cancellation.Token));

        Assert.Equal("visible", Assert.Single(await AssertMatchesExistingPresentationAsync(ReviewYear)).ScreenshotId);
    }

    [Fact]
    public async Task ReviewPreview_PropagatesFailureFromTheScreenshotProjection()
    {
        await SeedAsync("visible", At, exists: true);
        using (var connection = await Factory.OpenAsync())
        using (var command = connection.CreateCommand())
        {
            // COUNT(*) 仍可读取，失败必须来自截图字段投影，而不是前面的统计查询。
            command.CommandText = "ALTER TABLE screenshots RENAME COLUMN FilePath TO UnavailableFilePath";
            await command.ExecuteNonQueryAsync();
        }
        var queries = new ArchiveQueryService(new ArchiveService(Factory));

        var error = await Assert.ThrowsAsync<SqliteException>(() => queries.LoadReviewAsync(ReviewYear));

        Assert.Equal(1, error.SqliteErrorCode);
    }

    [Fact]
    public async Task CompleteGallery_StillReturnsAllYearsMissingFilesRawFieldsAndTags()
    {
        var original = new[]
        {
            await SeedAsync("present-current", At, exists: true),
            await SeedAsync("missing-current", At.AddMinutes(1), exists: false),
            await SeedAsync("present-previous", At.AddYears(-1), exists: true),
            await SeedAsync("missing-next", At.AddYears(1), exists: false),
        };
        var queries = new ArchiveQueryService(new ArchiveService(Factory));

        var gallery = await queries.LoadScreenshotsAsync();

        Assert.Equal(4, gallery.Screenshots.Count);
        Assert.Equal(4, gallery.Tags.Count);
        Assert.Equal(new[] { "missing-next", "missing-current", "present-current", "present-previous" },
            gallery.Screenshots.Select(item => item.ScreenshotId));
        Assert.Equal(2, gallery.Screenshots.Count(item => !item.FileExists));
        foreach (var expected in original)
        {
            AssertRawFields(expected, Assert.Single(gallery.Screenshots, item => item.ScreenshotId == expected.ScreenshotId));
            Assert.Equal(new[] { "shared", expected.ScreenshotId }.Order(StringComparer.OrdinalIgnoreCase),
                gallery.Tags[expected.ScreenshotId]);
        }
    }

    private async Task<IReadOnlyList<AnimeScreenshot>> AssertMatchesExistingPresentationAsync(int year)
    {
        var archive = new ArchiveService(Factory);
        var expected = (await archive.GetScreenshotsAsync(year: year))
            .Where(item => item.FileExists && item.CapturedAt.ToLocalTime().Year == year)
            .Take(2)
            .ToArray();

        var review = await new ArchiveQueryService(archive).LoadReviewAsync(year);

        Assert.Equal(year, review.Year);
        Assert.InRange(review.Screenshots.Count, 0, 2);
        Assert.Equal(expected.Length, review.Screenshots.Count);
        for (var index = 0; index < expected.Length; index++)
            AssertRawFields(expected[index], review.Screenshots[index]);
        return review.Screenshots;
    }

    private async Task<AnimeScreenshot> SeedAsync(string id, DateTimeOffset capturedAt, bool exists)
    {
        var index = ++_seedCount;
        var file = Path.Combine(Path.GetDirectoryName(Factory.DatabasePath)!, id + ".presence");
        if (exists)
            await File.WriteAllBytesAsync(file, []);
        var hasContext = index % 2 == 0;
        var item = new AnimeScreenshot(id, file, "stored-" + id, capturedAt,
            "Window " + id, "Process " + index, 640 + index, 360 + index,
            hasContext ? -7 : null, hasContext ? "作品 " + id : null,
            hasContext ? index : null, hasContext ? index + 0.25 : null,
            "备注 " + id, exists);
        var archive = new ArchiveService(Factory);
        await archive.InsertScreenshotAsync(item);
        if (capturedAt.Offset != TimeSpan.Zero)
        {
            // 正常写入归一到 UTC；这里单独种入旧导入允许保留的 offset。
            using var connection = await Factory.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE screenshots SET CapturedAt=@at WHERE ScreenshotId=@id";
            command.Parameters.AddWithValue("@at", capturedAt.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("@id", id);
            await command.ExecuteNonQueryAsync();
        }
        await archive.AddScreenshotTagsAsync([id], ["shared", id]);
        return item;
    }

    private static DateTimeOffset LocalYearStart(int year)
        => new(new DateTime(year, 1, 1), TimeZoneInfo.Local.GetUtcOffset(new DateTime(year, 1, 1)));

    private static void AssertRawFields(AnimeScreenshot expected, AnimeScreenshot actual)
    {
        Assert.Equal(expected.ScreenshotId, actual.ScreenshotId);
        Assert.Equal(expected.FilePath, actual.FilePath);
        Assert.Equal(expected.Sha256, actual.Sha256);
        Assert.Equal(expected.CapturedAt, actual.CapturedAt);
        Assert.Equal(expected.CapturedAt.Offset, actual.CapturedAt.Offset);
        Assert.Equal(expected.WindowTitle, actual.WindowTitle);
        Assert.Equal(expected.ProcessName, actual.ProcessName);
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        Assert.Equal(expected.AnimeId, actual.AnimeId);
        Assert.Equal(expected.AnimeTitle, actual.AnimeTitle);
        Assert.Equal(expected.EpisodeNumber, actual.EpisodeNumber);
        Assert.Equal(expected.PlaybackPositionSeconds, actual.PlaybackPositionSeconds);
        Assert.Equal(expected.ContextNote, actual.ContextNote);
        Assert.Equal(expected.FileExists, actual.FileExists);
    }
}
