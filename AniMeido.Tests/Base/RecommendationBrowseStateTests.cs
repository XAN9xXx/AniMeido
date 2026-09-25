using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;

namespace AniMeido.Tests;

public class RecommendationBrowseStateTests
{
    [Theory]
    [InlineData(23, true)]
    [InlineData(24, true)]
    [InlineData(25, false)]
    public void SnapshotFreshness_UsesTheSame24HourBoundary(int ageHours, bool expected)
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = Snapshot() with { GeneratedAt = now.AddHours(-ageHours) };
        Assert.Equal(expected, RecommendationService.IsSnapshotFresh(snapshot, now));
        Assert.False(RecommendationService.IsSnapshotFresh(snapshot with { SchemaVersion = 1 }, now));
    }

    [Fact]
    public void Skip_FiltersVisibleItemsWithoutChangingSnapshot()
    {
        var snapshot = Snapshot();
        var state = new RecommendationBrowseState { Snapshot = snapshot };
        state.SkippedIds.Add(2);
        state.RemovedIds.Add(3);
        Assert.Equal(1, Assert.Single(state.VisibleItems).Anime.ID);
        Assert.Equal(3, snapshot.Items.Count);
    }

    [Fact]
    public void NewRound_ClearsTemporaryExclusionsAndPendingFlag()
    {
        var state = new RecommendationBrowseState { HasPendingPreferences = true, VerticalOffset = 200 };
        state.SkippedIds.Add(1);
        state.RemovedIds.Add(2);
        state.StartRound(Snapshot());
        Assert.Empty(state.SkippedIds);
        Assert.Empty(state.RemovedIds);
        Assert.False(state.HasPendingPreferences);
        Assert.Equal(0, state.VerticalOffset);
        Assert.Equal(3, state.VisibleItems.Count);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(9, 3)]
    public void RemovedSelection_UsesNextPositionThenLastItem(int index, int expected)
        => Assert.Equal(expected, RecommendationBrowseState.SelectAfterRemoval(Snapshot().Items, index));

    [Fact]
    public void EmptyList_HasNoSelection()
        => Assert.Null(RecommendationBrowseState.SelectAfterRemoval([], 0));

    [Fact]
    public void WeakEvidence_IsNotDescribedAsAnExplicitLike()
    {
        var feature = new RecommendationFeature(RecommendationFeatureKind.Tag, "旅行", "旅行");
        var profile = new RecommendationFeatureProfile(feature, 2, null,
            [new RecommendationEvidence(9, "浏览过的作品", 2)]);
        var result = RecommendationScorer.Rank([profile],
            [new RecommendationCandidate(Item(1).Anime, [feature])], new DateOnly(2026, 9, 12));
        Assert.Contains("你记录过的", Assert.Single(result).PrimaryReason);
        Assert.DoesNotContain("因为你喜欢", result[0].PrimaryReason);
    }

    [Fact]
    public void ExplicitLike_KeepsItsAccurateExplanation()
    {
        var feature = new RecommendationFeature(RecommendationFeatureKind.Tag, "旅行", "旅行");
        var profile = new RecommendationFeatureProfile(feature, 0, RecommendationAdjustment.Like, []);
        var result = RecommendationScorer.Rank([profile],
            [new RecommendationCandidate(Item(1).Anime, [feature])], new DateOnly(2026, 9, 12));
        Assert.Contains("设为喜欢", Assert.Single(result).PrimaryReason);
    }

    private static RecommendationSnapshot Snapshot() => new(
        RecommendationSnapshot.CurrentSchemaVersion, DateTimeOffset.UtcNow, true, [Item(1), Item(2), Item(3)]);

    private static RecommendationItem Item(int id) => new(
        new Anime(id, $"作品{id}", null, [], new DateOnly(2026, 1, 1), null, "", 2026, 1),
        1, [], true, true);
}

public class RecommendationFollowingTests : DbTestBase
{
    [Fact]
    public async Task Follow_DoesNotOverwriteAnExistingStatus()
    {
        await RunProductionMigrationAsync();
        var tracking = new TrackingService(DbFactory);
        await tracking.SetStatusAsync(42, AnimeTrackingStatus.Watching);
        Assert.Equal((AnimeTrackingStatus.Watching, false),
            await tracking.ToggleFollowingAsync(42, CancellationToken.None));
        Assert.Equal(AnimeTrackingStatus.Watching, await tracking.GetStatusAsync(42));
    }

    [Fact]
    public async Task Follow_SecondCallRemovesOnlyFollowingStatus()
    {
        await RunProductionMigrationAsync();
        var tracking = new TrackingService(DbFactory);
        Assert.Equal((AnimeTrackingStatus.Following, true),
            await tracking.ToggleFollowingAsync(42, CancellationToken.None));
        Assert.Equal((null, true),
            await tracking.ToggleFollowingAsync(42, CancellationToken.None));
        Assert.Null(await tracking.GetStatusAsync(42));
        using var connection = await DbFactory.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM tracking_events WHERE AnimeId = 42";
        Assert.Equal(1L, Convert.ToInt64(await command.ExecuteScalarAsync()));
        Assert.Empty(await new ActionCenterService(DbFactory).GetPlansAsync());
    }
}
