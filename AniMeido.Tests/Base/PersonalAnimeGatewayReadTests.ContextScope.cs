using AniMeido.Contracts.PersonalAnime;

using AniMeido.Contracts.Models;

namespace AniMeido.Tests;

public sealed partial class PersonalAnimeGatewayReadTests
{
    [Theory]
    [InlineData(PersonalAnimeDataCategory.None)]
    [InlineData(PersonalAnimeDataCategory.PublicMetadata)]
    [InlineData(PersonalAnimeDataCategory.Tracking)]
    [InlineData(PersonalAnimeDataCategory.PlansAndProgress)]
    [InlineData(PersonalAnimeDataCategory.PersonalRating)]
    [InlineData(PersonalAnimeDataCategory.ArchiveTextAndHistory)]
    [InlineData(PersonalAnimeDataCategory.BrowseSummary)]
    [InlineData(PersonalAnimeDataCategory.Tracking | PersonalAnimeDataCategory.PlansAndProgress)]
    [Trait("Category", "Mechanism")]
    public async Task Context_ReadsTrackingAndProgressOnlyWhenRequested(PersonalAnimeDataCategory category)
    {
        await RunProductionMigrationAsync();
        await using var fixture = new ReadFixture(DbFactory);
        await SeedAsync(fixture);
        var needsTracking = category.HasFlag(PersonalAnimeDataCategory.Tracking);
        var needsProgress = category.HasFlag(PersonalAnimeDataCategory.PlansAndProgress);
        using (var connection = await DbFactory.OpenAsync())
        using (var command = connection.CreateCommand())
        {
            // 标题仍依赖计划与浏览；只移除本次未请求且不参与标题的两张表。
            if (!needsTracking)
            {
                command.CommandText = "DROP TABLE tracking";
                await command.ExecuteNonQueryAsync();
            }
            if (!needsProgress)
            {
                command.CommandText = "DROP TABLE anime_progress";
                await command.ExecuteNonQueryAsync();
            }
        }

        await GuardAgainstWritesAsync();
        var before = await SnapshotAsync();
        var result = await fixture.Gateway.BuildContextAsync(new("read scope", [17], category));

        var item = Assert.Single(result.Items);
        Assert.Equal(17, item.AnimeId);
        Assert.Equal(category switch
        {
            PersonalAnimeDataCategory.PublicMetadata => "Public Alpha",
            PersonalAnimeDataCategory.PersonalRating or PersonalAnimeDataCategory.ArchiveTextAndHistory => "Alpha Series",
            _ => "Plan Alpha",
        }, item.Title);
        Assert.Equal(needsTracking ? AnimeTrackingStatus.Watching : (AnimeTrackingStatus?)null,
            item.TrackingStatus);
        if (needsProgress)
        {
            Assert.NotNull(item.Plan);
            Assert.NotNull(item.Progress);
            Assert.Equal(3, item.Progress.CurrentEpisode);
            Assert.Equal(420, item.Progress.PositionSeconds);
        }
        else
        {
            Assert.Null(item.Plan);
            Assert.Null(item.Progress);
        }
        Assert.Equal(before, await SnapshotAsync());
    }

    [Theory]
    [InlineData(PersonalAnimeDataCategory.SavedBangumiTags)]
    [InlineData(PersonalAnimeDataCategory.RecommendationProfile)]
    [Trait("Category", "Mechanism")]
    public async Task ProfileOnlyContext_DoesNotReadPerAnimeTables(PersonalAnimeDataCategory category)
    {
        await RunProductionMigrationAsync();
        await using var fixture = new ReadFixture(DbFactory);
        await fixture.Cache.CleanExpiredAsync();
        await fixture.Saved.SaveTagAsync("Saved Tag");
        using (var connection = await DbFactory.OpenAsync())
        using (var command = connection.CreateCommand())
        {
            // 移除无关表，让误读明确失败；这里验证读取范围，不模拟真实数据库损坏。
            command.CommandText = """
                DROP TABLE tracking;
                DROP TABLE anime_plans;
                DROP TABLE anime_progress;
                DROP TABLE browse_history;
                """;
            await command.ExecuteNonQueryAsync();
        }

        var result = await fixture.Gateway.BuildContextAsync(new("profile only", [], category));

        Assert.Empty(result.Items);
        Assert.Empty(result.PreferenceProfile);
        Assert.Equal(category == PersonalAnimeDataCategory.SavedBangumiTags ? new[] { "Saved Tag" } : [],
            result.SavedBangumiTags);
        Assert.Equal(0, fixture.Source.Calls);
    }

    [Fact]
    public async Task ProfileOnlyContext_PreCancelledRequestRemainsCancelled()
    {
        await RunProductionMigrationAsync();
        await using var fixture = new ReadFixture(DbFactory);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Gateway.BuildContextAsync(
            new("cancelled", [], PersonalAnimeDataCategory.SavedBangumiTags), cancellation.Token));
    }
}
