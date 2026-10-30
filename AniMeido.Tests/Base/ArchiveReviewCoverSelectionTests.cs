using AniMeido.Plugin.Base.Services;

namespace AniMeido.Tests;

public sealed class ArchiveReviewCoverSelectionTests : DbTestBase
{
    private const int ReviewYear = 2025;

    [Fact]
    public async Task ReviewCovers_KeepThreeNewestCreatedArchivesAndOriginalUpdatedOrderForTies()
    {
        await RunProductionMigrationAsync();
        var created = new DateTimeOffset(ReviewYear, 6, 1, 0, 0, 0, TimeSpan.Zero);
        await SeedArchiveAsync(12, "较早新建、较晚更新", created.AddDays(-1), created.AddMonths(2));
        await SeedArchiveAsync(11, "同日较早更新", created, created.AddDays(1));
        await SeedArchiveAsync(10, "同日较晚更新", created, created.AddDays(2));
        await SeedArchiveAsync(-7, "本地保留记录", created, created.AddDays(3));
        await SeedArchiveAsync(20, "其他年度", created.AddYears(1), created.AddYears(2));

        var covers = await ReadCoversAsync(ReviewYear);

        Assert.Equal(new[] { -7, 10, 11 }, covers.Select(item => item.AnimeId));
        Assert.Equal(new[] { "本地保留记录", "同日较晚更新", "同日较早更新" }, covers.Select(item => item.Title));
    }

    [Fact]
    public async Task ReviewCovers_UseLocalYearBoundariesWithoutLosingStoredOffsetOrSubsecondPrecision()
    {
        await RunProductionMigrationAsync();
        var start = new DateTimeOffset(new DateTime(ReviewYear, 1, 1, 0, 0, 0, DateTimeKind.Local));
        var end = new DateTimeOffset(new DateTime(ReviewYear + 1, 1, 1, 0, 0, 0, DateTimeKind.Local));
        var lastTick = end.AddTicks(-1).ToOffset(TimeSpan.FromHours(12));
        await SeedArchiveAsync(1, "年度开始", start.ToOffset(TimeSpan.FromHours(14)), start);
        await SeedArchiveAsync(2, "上一年度最后一刻", start.AddTicks(-1).ToOffset(TimeSpan.FromHours(-12)), start);
        await SeedArchiveAsync(3, "年度最后一刻", lastTick, end);
        await SeedArchiveAsync(4, "下一年度开始", end.ToOffset(TimeSpan.FromHours(-14)), end);

        var covers = await ReadCoversAsync(ReviewYear);

        Assert.Equal(new[] { 3, 1 }, covers.Select(item => item.AnimeId));
        Assert.Equal(lastTick, covers[0].CreatedAt);
        Assert.Equal(TimeSpan.FromHours(12), covers[0].CreatedAt.Offset);
    }

    [Fact]
    public async Task ReviewCovers_EmptyYearReturnsNoCovers()
    {
        await RunProductionMigrationAsync();
        var at = new DateTimeOffset(ReviewYear - 1, 6, 1, 0, 0, 0, TimeSpan.Zero);
        await SeedArchiveAsync(1, "往年记录", at, at);

        Assert.Empty(await ReadCoversAsync(ReviewYear));
    }

    private async Task<IReadOnlyList<(int AnimeId, string Title, DateTimeOffset CreatedAt)>> ReadCoversAsync(int year)
    {
        var query = new ArchiveQueryService(new ArchiveService(DbFactory));
        var review = await query.LoadReviewAsync(year);
        return review.Covers
            .Select(item => (item.AnimeId, item.Title, item.CreatedAt))
            .ToArray();
    }

    private async Task SeedArchiveAsync(int animeId, string title, DateTimeOffset createdAt, DateTimeOffset updatedAt)
    {
        using var connection = await DbFactory.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO anime_archives(AnimeId, TitleSnapshot, SummaryNote, CreatedAt, UpdatedAt)
            VALUES(@id, @title, '完整保留的笔记', @created, @updated)
            """;
        command.Parameters.AddWithValue("@id", animeId);
        command.Parameters.AddWithValue("@title", title);
        command.Parameters.AddWithValue("@created", createdAt.ToString("O"));
        command.Parameters.AddWithValue("@updated", updatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }
}
