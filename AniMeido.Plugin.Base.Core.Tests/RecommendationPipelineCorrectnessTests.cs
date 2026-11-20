using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;

namespace AniMeido.Tests;

public sealed class RecommendationPipelineCorrectnessTests : IAsyncLifetime
{
    private readonly PersonalDatabaseFixture _database = new();
    public Task InitializeAsync() => _database.InitializeAsync();
    public Task DisposeAsync() => _database.DisposeAsync();
    private static RecommendationFeatureProfile Positive => new(new(RecommendationFeatureKind.Tag, "A", "A"), 2, null, []);
    private static RecommendationFeatureProfile Negative => new(new(RecommendationFeatureKind.Tag, "B", "B"), 0, RecommendationAdjustment.Reduce, []);
    private static RecommendationCandidateProvider Provider(Source source) => new(source, NullLogger<RecommendationCandidateProvider>.Instance);

    [Fact]
    public async Task UnshownEnrichedCandidatesAreReusedByTheNextWindow()
    {
        var source = new Source();
        await using var provider = Provider(source);
        var round = provider.CreateRound([Positive], new HashSet<int>());
        var first = await provider.GetNextWindowAsync(round, 30, CancellationToken.None);
        round.Consume(first.Take(20).Select(x => x.Anime.ID));
        var second = await provider.GetNextWindowAsync(round, 30, CancellationToken.None);
        Assert.Equal(20, second.Count);
        Assert.All(source.Calls.Values, count => Assert.Equal(1, count));
    }

    [Fact]
    public async Task FailedNegativeCategoryIsDeferredThenRetriedWithoutRepeatingSuccessfulCategories()
    {
        var source = new Source { Count = 1, FailTagsOnce = true };
        await using var provider = Provider(source);
        var round = provider.CreateRound([Positive, Negative], new HashSet<int>());
        var first = await provider.GetNextWindowAsync(round, 30, CancellationToken.None);
        Assert.Empty(RecommendationScorer.Rank([Positive, Negative], first, DateOnly.FromDateTime(DateTime.Today)));
        var second = await provider.GetNextWindowAsync(round, 30, CancellationToken.None);
        Assert.Contains(Assert.Single(second).Features, f => f.Key == "B");
        Assert.Equal(2, source.Calls[(1, "tags")]);
        Assert.Equal(1, source.Calls[(1, "detail")]);
        Assert.Equal(1, source.Calls[(1, "studios")]);
        Assert.Equal(1, source.Calls[(1, "actors")]);
    }

    [Fact]
    public async Task RepeatedCategoryFailureHasABoundedRetryAndLeavesNoStuckRound()
    {
        var source = new Source { Count = 1, AlwaysFailTags = true };
        await using var provider = Provider(source);
        var round = provider.CreateRound([Positive, Negative], new HashSet<int>());
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var window = await provider.GetNextWindowAsync(round, 30, CancellationToken.None);
            Assert.Empty(RecommendationScorer.Rank([Positive, Negative], window, DateOnly.FromDateTime(DateTime.Today)));
        }
        Assert.Equal(2, source.Calls[(1, "tags")]);
        Assert.False(round.HasMore);
    }

    [Fact]
    public async Task PopularFallbackStillUsesNegativePreferencesWithinOneEnrichmentBudget()
    {
        var source = new Source { Count = 40 };
        await using var provider = Provider(source);
        await using var service = RecommendationTestServices.CreateService(_database.Factory, provider);
        await service.SetFeaturePreferenceAsync(Negative.Feature, RecommendationAdjustment.Reduce);
        var generation = Assert.IsType<RecommendationGeneration>(await service.RefreshAsync());
        Assert.Equal(20, generation.Snapshot.Items.Count);
        Assert.All(generation.Snapshot.Items.Take(10), item => Assert.True(item.Anime.ID > 20));
        Assert.DoesNotContain(generation.Snapshot.Items, item => item.ReasonSummary.Contains("标记的作品还不多"));
        Assert.InRange(source.Calls.Keys.Count(key => key.Kind == "tags"), 1, 30);
        Assert.DoesNotContain(source.Calls.Keys, key => key.Kind is "actors" or "studios");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReturnedSnapshotDropsHiddenAndRemovedOldItemsEvenWithoutMorePages(bool hasMore)
    {
        await using var provider = Provider(new Source());
        await using var service = RecommendationTestServices.CreateService(_database.Factory, provider);
        await service.HideAnimeAsync(1, "作品1");
        var snapshot = new RecommendationSnapshot(RecommendationSnapshot.CurrentSchemaVersion, DateTimeOffset.UtcNow,
            true, [new(Source.Anime(1), 1, [], true, true), new(Source.Anime(2), 1, [], true, true)], [Positive], hasMore);
        var result = await service.LoadMoreAsync(snapshot, new HashSet<int> { 2 });
        Assert.DoesNotContain(result.Snapshot.Items, item => item.Anime.ID is 1 or 2);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(9)]
    [InlineData(10)]
    public async Task OldAlgorithmRoundCannotContinueEvenWhenPassedDirectly(int schema)
    {
        await using var provider = RecommendationTestServices.CreateCandidates();
        await using var service = RecommendationTestServices.CreateService(_database.Factory, provider);
        var snapshot = new RecommendationSnapshot(schema, DateTimeOffset.UtcNow, true, [], [Positive], true);
        var result = await service.LoadMoreAsync(snapshot, new HashSet<int>());
        Assert.Empty(result.Items);
        Assert.False(result.Snapshot.HasMore);
    }

    [Fact]
    public async Task RoundUsesItsOriginalDayForQueryAndRanking()
    {
        var source = new Source();
        await using var provider = Provider(source);
        var date = new DateOnly(2026, 11, 11);
        var round = provider.CreateRound([Positive], new HashSet<int>(), date);
        await provider.GetNextWindowAsync(round, 30, CancellationToken.None);
        Assert.Equal(date, round.Today);
        Assert.Contains(source.Ranges, range => range.From == "2023-11-11");
        Assert.Contains(source.Ranges, range => range.To == "2023-11-11");
    }

    [Fact]
    public async Task CancellationKeepsSuccessfulDetailAndDoesNotSpendTheFeatureRetry()
    {
        var source = new Source { Count = 1, PauseFirstTag = true };
        await using var provider = Provider(source);
        var round = provider.CreateRound([Positive, Negative], new HashSet<int>());
        using var cancellation = new CancellationTokenSource();
        var reading = provider.GetNextWindowAsync(round, 30, cancellation.Token);
        await source.TagStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
        var next = await provider.GetNextWindowAsync(round, 30, CancellationToken.None);
        Assert.Single(next);
        Assert.Equal(1, source.Calls[(1, "detail")]);
        Assert.Equal(2, source.Calls[(1, "tags")]);
    }

    [Fact]
    public async Task HidingAnOldItemDuringEnrichmentAlsoFiltersTheReturnedSnapshot()
    {
        var source = new Source { Count = 2, PauseFirstTag = true };
        await using var provider = Provider(source);
        await using var service = RecommendationTestServices.CreateService(_database.Factory, provider);
        var snapshot = new RecommendationSnapshot(RecommendationSnapshot.CurrentSchemaVersion, DateTimeOffset.UtcNow,
            true, [new(Source.Anime(1), 2, [], true, true)], [Positive], true);
        var loading = service.LoadMoreAsync(snapshot, new HashSet<int>());
        try
        {
            await source.TagStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await service.HideAnimeAsync(1, "作品1");
        }
        finally { source.ReleaseTag.TrySetResult(); }
        var result = await loading;
        Assert.DoesNotContain(result.Snapshot.Items, item => item.Anime.ID == 1);
        Assert.Contains(result.Snapshot.Items, item => item.Anime.ID == 2);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(5.5)]
    [InlineData(8.5)]
    public async Task RatedWorksAreExcludedWithoutTrackingButUnratedArchivesRemain(double rating)
    {
        await RateAsync(1, rating);
        await RateAsync(2, null);
        await using var provider = Provider(new Source { Count = 3 });
        await using var service = RecommendationTestServices.CreateService(_database.Factory, provider);
        var result = Assert.IsType<RecommendationGeneration>(await service.RefreshAsync());
        Assert.DoesNotContain(result.Snapshot.Items, item => item.Anime.ID == 1);
        Assert.Contains(result.Snapshot.Items, item => item.Anime.ID == 2);
    }

    [Fact]
    public async Task ReadingCacheAndReusingPreviousBatchBothFilterNewRatings()
    {
        await using var provider = Provider(new Source { Count = 1 });
        await using var service = RecommendationTestServices.CreateService(_database.Factory, provider);
        var first = Assert.IsType<RecommendationGeneration>(await service.RefreshAsync());
        Assert.Single(first.Snapshot.Items);
        await RateAsync(1, 5.5);
        var cached = Assert.IsType<RecommendationSnapshot>(await service.GetCachedSnapshotAsync(true));
        Assert.Empty(cached.Items);
        var refreshed = Assert.IsType<RecommendationGeneration>(await service.RefreshAsync(preferNewBatch: true));
        Assert.Empty(refreshed.Snapshot.Items);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RatingsWrittenDuringRemoteReadFilterOldAndNewItems(bool loadMore)
    {
        var source = new Source { Count = 3, PauseFirstTag = true };
        await using var provider = Provider(source);
        await using var service = RecommendationTestServices.CreateService(_database.Factory, provider);
        await service.SetFeaturePreferenceAsync(Positive.Feature, RecommendationAdjustment.Like);
        var snapshot = new RecommendationSnapshot(RecommendationSnapshot.CurrentSchemaVersion, DateTimeOffset.UtcNow,
            true, [new(Source.Anime(1), 2, [], true, true)], [Positive], true);
        var refreshing = loadMore ? null : service.RefreshAsync();
        var loading = loadMore ? service.LoadMoreAsync(snapshot, new HashSet<int>()) : null;
        try
        {
            await source.TagStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await RateAsync(1, 8);
            await RateAsync(2, 8);
        }
        finally { source.ReleaseTag.TrySetResult(); }
        var result = loadMore ? (await loading!).Snapshot : (await refreshing!)!.Snapshot;
        Assert.DoesNotContain(result.Items, item => item.Anime.ID is 1 or 2);
        Assert.Contains(result.Items, item => item.Anime.ID == 3);
    }

    private async Task RateAsync(int id, double? rating)
    {
        await using var connection = await _database.Factory.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO anime_archives(AnimeId, TitleSnapshot, PersonalRating, SummaryNote, CreatedAt, UpdatedAt)
            VALUES(@id, '评分测试', @rating, '', @at, @at)
            ON CONFLICT(AnimeId) DO UPDATE SET PersonalRating=excluded.PersonalRating;
            """;
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@rating", (object?)rating ?? DBNull.Value);
        command.Parameters.AddWithValue("@at", "2026-11-12T00:00:00Z");
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task FilteringAnInMemoryRoundPreservesProfileAndContinuationWithoutNetwork()
    {
        await using var provider = RecommendationTestServices.CreateCandidates();
        await using var service = RecommendationTestServices.CreateService(_database.Factory, provider);
        var snapshot = new RecommendationSnapshot(RecommendationSnapshot.CurrentSchemaVersion, DateTimeOffset.UtcNow,
            true, [new(Source.Anime(1), 2, [], true, true), new(Source.Anime(2), 2, [], true, true)], [Positive], true);
        await RateAsync(1, 8);
        var filtered = await service.FilterRatedItemsAsync(snapshot);
        Assert.Equal(2, Assert.Single(filtered.Items).Anime.ID);
        Assert.Equal(snapshot.GeneratedAt, filtered.GeneratedAt);
        Assert.Same(snapshot.RoundProfile, filtered.RoundProfile);
        Assert.True(filtered.HasMore);
        Assert.True(filtered.IsPersonalized);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.FilterRatedItemsAsync(snapshot, cancellation.Token));
    }

    private sealed class Source : IAnimeDataSource
    {
        public int Count { get; init; } = 40;
        public bool FailTagsOnce { get; init; }
        public bool AlwaysFailTags { get; init; }
        public bool PauseFirstTag { get; init; }
        public TaskCompletionSource TagStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseTag { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentDictionary<(int Id, string Kind), int> Calls { get; } = new();
        public ConcurrentBag<(string? From, string? To)> Ranges { get; } = [];
        private int Record(int id, string kind) => Calls.AddOrUpdate((id, kind), 1, (_, value) => value + 1);
        public static Anime Anime(int id) => new(id, $"作品{id}", null, [], DateOnly.FromDateTime(DateTime.Today), null, "", 2026, 10, Score: id <= 20 ? 9 : 8);
        public Task<Anime?> GetAnimeDetailAsync(int id, CancellationToken ct)
        { Record(id, "detail"); return Task.FromResult<Anime?>(Anime(id)); }
        public async Task<List<Tag>> GetTagsAsync(int id, CancellationToken ct)
        {
            var attempt = Record(id, "tags");
            if (PauseFirstTag && attempt == 1)
            {
                TagStarted.TrySetResult();
                await ReleaseTag.Task.WaitAsync(ct);
            }
            if (AlwaysFailTags || (FailTagsOnce && attempt == 1)) throw new HttpRequestException("synthetic failure");
            return id <= 20 ? [new("A"), new("B")] : [new("A")];
        }
        public Task<List<Studio>> GetStudioAsync(int id, CancellationToken ct)
        { Record(id, "studios"); return Task.FromResult(new List<Studio>()); }
        public Task<List<VoiceActor>> GetCVsAsync(int id, CancellationToken ct)
        { Record(id, "actors"); return Task.FromResult(new List<VoiceActor>()); }
        public Task<List<Anime>> GetAnimeBySeasonAsync(int year, Season season, CancellationToken ct)
            => Task.FromResult(Enumerable.Range(1, Count).Select(Anime).ToList());
        public Task<(List<Anime> Results, int Total)> SearchByTagAsync(string tag, int offset, string sort,
            CancellationToken ct, string? airDateFrom = null, string? airDateTo = null)
        {
            Ranges.Add((airDateFrom, airDateTo));
            return Task.FromResult((airDateFrom is null ? new List<Anime>() : Enumerable.Range(1, Count).Skip(offset).Select(Anime).ToList(), airDateFrom is null ? 0 : Count));
        }
        public Task<List<Anime>> GetCurrentBroadcastScheduleAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<List<CharacterRole>> GetCharacterRolesAsync(int id, CancellationToken ct) => throw new NotSupportedException();
        public Task<List<PersonWork>> GetPersonWorksAsync(int id, CancellationToken ct) => throw new NotSupportedException();
        public Task<(List<Anime> Results, int Total, int NextOffset)> SearchByKeywordAsync(string keyword, int offset, CancellationToken ct) => throw new NotSupportedException();
    }
}
