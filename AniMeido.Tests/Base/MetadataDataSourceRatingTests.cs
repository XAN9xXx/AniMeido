using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;

namespace AniMeido.Tests;

public sealed partial class MetadataDataSourceTests
{
    [Theory]
    [InlineData(49, "2026-10", false)]
    [InlineData(50, "2026-10", true)]
    [InlineData(500, "2026-11", false)]
    [InlineData(500, "2027", false)]
    [InlineData(500, null, true)]
    public async Task DetailAndPersonWorks_MapCompositeOrNull(int votes, string? date, bool eligible)
    {
        await CreateBaseTablesAsync();
        MetadataRating[] ratings = [new("mal", 8.5, votes)];
        using var handler = new StubHandler(r => r.RequestUri!.AbsolutePath == "/v1/works/1"
            ? Reply(new { data = Work(1, date, ratings) })
            : Reply(new { total = 1, items = new[] { new { work = new MetadataBrief { Id = 1, FirstDate = date, Ratings = [.. ratings] } } } }));
        using var client = Client(handler);
        var source = Source(client);
        var anime = Assert.IsType<Anime>(await source.GetAnimeDetailAsync(1, default));
        var person = Assert.Single(await source.GetPersonWorksAsync(7, default));
        var expected = CompositeRating.Calculate(ratings, date, new(2026, 10, 19))?.Score;
        Assert.Equal(eligible, anime.Score.HasValue);
        Assert.Equal(expected, anime.Score);
        Assert.Equal(expected, person.Score);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SameCachedDetail_RemapsAniListOnDay15And16EvenWhenStale(bool expired)
    {
        await CreateBaseTablesAsync();
        var time = new TestTime { Now = new(2026, 11, 5, 0, 0, 0, TimeSpan.Zero) };
        var cache = new CacheService(DbFactory);
        MetadataRating[] ratings = [new("anilist", 80, 100, "2026-10-21")];
        var offline = false;
        using var handler = new StubHandler(_ => offline ? throw new HttpRequestException("synthetic offline")
            : Reply(new { data = Work(1, null, ratings) }));
        using var client = Client(handler);
        var source = Source(client, cache, time);
        Assert.NotNull((await source.GetAnimeDetailAsync(1, default))!.Score);
        Assert.Contains("AniList　80 分", await source.GetRatingTextAsync(1, default));
        if (expired)
        {
            var json = Assert.IsType<string>(await cache.GetCacheAsync("metadata:detail:1"));
            await cache.SetCacheAsync("metadata:detail:1", json, TimeSpan.FromSeconds(-1));
            offline = true;
        }
        time.Now = time.Now.AddDays(1);
        Assert.Null((await source.GetAnimeDetailAsync(1, default))!.Score);
        Assert.Empty(await source.GetRatingTextAsync(1, default));
        if (!expired) Assert.Single(handler.GetPaths);
    }

    [Fact]
    public async Task CachedPersonWorksAndBriefRank_RecomputeAfterAniListExpires()
    {
        await CreateBaseTablesAsync();
        var time = new TestTime { Now = new(2026, 11, 5, 0, 0, 0, TimeSpan.Zero) };
        var works = new[]
        {
            new MetadataBrief { Id = 1, Ratings = [new("anilist", 90, 500, "2026-10-21")] },
            new MetadataBrief { Id = 2, Ratings = [new("bangumi", 7, 500)] },
        };
        using var handler = new StubHandler(r =>
        {
            if (r.RequestUri!.AbsolutePath == "/v1/vocab") return Vocab(r);
            if (r.RequestUri.AbsolutePath == "/v1/brief") return Reply(new { total = 2, items = works });
            if (r.RequestUri.AbsolutePath.StartsWith("/v1/participants/"))
                return Reply(new { total = 2, items = works.Select(work => new { work }) });
            return Reply(new { total = 2, items = works.Select(work => Work(work.Id, null, work.Ratings)) });
        });
        using var client = Client(handler);
        var source = Source(client, time: time);
        Assert.Equal(new[] { 1, 2 }, (await source.SearchByTagAsync("科幻", 0, "rank", default)).Results.Select(a => a.ID));
        Assert.NotNull((await source.GetPersonWorksAsync(7, default))[0].Score);
        time.Now = time.Now.AddDays(1);
        var ranked = (await source.SearchByTagAsync("科幻", 0, "rank", default)).Results;
        Assert.Equal(new[] { 2, 1 }, ranked.Select(a => a.ID));
        Assert.Null(ranked[1].Score);
        Assert.Null((await source.GetPersonWorksAsync(7, default))[0].Score);
        Assert.Single(handler.GetPaths, path => path.StartsWith("/v1/brief"));
        Assert.Single(handler.GetPaths, path => path.StartsWith("/v1/participants/"));
    }

    [Fact]
    public async Task RankAndLocalSort_UseCompositeBothDirectionsWithUnrankedLastAndStableFallback()
    {
        await CreateBaseTablesAsync();
        var briefs = new[]
        {
            new MetadataBrief { Id = 1, Ratings = [new("bangumi", 9, 49)] },
            new MetadataBrief { Id = 2, Ratings = [new("bangumi", 7, 1000), new("mal", 9, 1000)] },
            new MetadataBrief { Id = 3, Ratings = [new("bangumi", 8, 50)] },
            new MetadataBrief { Id = 4, Ratings = [new("bangumi", 8, 20)] },
            new MetadataBrief { Id = 5, Ratings = [new("bangumi", 8, 30)] },
            new MetadataBrief { Id = 6, Ratings = [new("bangumi", 8, 30)] },
            new MetadataBrief { Id = 7 },
        };
        using var handler = TagHandler(briefs);
        using var client = Client(handler);
        var result = (await Source(client).SearchByTagAsync("科幻", 0, "rank", default)).Results;
        Assert.Equal(new[] { 2, 3, 1, 5, 6, 4, 7 }, result.Select(a => a.ID));
        Assert.Equal(new[] { 2, 3, 1, 5, 6, 4, 7 }, TagSearchResults.Sort(result, "rank", true).Select(a => a.ID));
        Assert.Equal(new[] { 3, 2, 1, 5, 6, 4, 7 }, TagSearchResults.Sort(result, "rank", false).Select(a => a.ID));
        Assert.All(result.Skip(2), anime => Assert.Null(anime.Score));
    }

    [Fact]
    public async Task Rank_UsesUnroundedDifferenceOfHalfAThousandthAndExactTieBreakers()
    {
        await CreateBaseTablesAsync();
        var briefs = new[]
        {
            new MetadataBrief { Id = 1, Ratings = [new("bangumi", 8, 1000)] },
            new MetadataBrief { Id = 2, Ratings = [new("mal", 8.657525, 1000)] },
            new MetadataBrief { Id = 3, Ratings = [new("mal", 8.657, 1000)] },
            new MetadataBrief { Id = 4, Ratings = [new("bangumi", 8, 1000)] },
            new MetadataBrief { Id = 5, Ratings = [new("bangumi", 6.33, 200)] },
            new MetadataBrief { Id = 6, Ratings = [new("bangumi", 6.33, 100)] },
            new MetadataBrief { Id = 7, Ratings = [new("bangumi", 6.33, 200)] },
            new MetadataBrief { Id = 8 },
        };
        using var handler = TagHandler(briefs);
        using var client = Client(handler);
        var result = (await Source(client).SearchByTagAsync("科幻", 0, "rank", default)).Results;
        Assert.Equal(new[] { 2, 1, 4, 3, 5, 7, 6, 8 }, result.Select(anime => anime.ID));
        Assert.Equal(0.0005, result[0].Score!.Value - result[1].Score!.Value, 12);
        Assert.Equal(result[1].Score, result[3].Score);
        Assert.Equal(result[4].Score, result[6].Score);
        Assert.Equal(new[] { 2, 1, 4, 3, 5, 7, 6, 8 }, TagSearchResults.Sort(result, "rank", true).Select(anime => anime.ID));
        Assert.Equal(new[] { 5, 7, 6, 1, 4, 3, 2, 8 }, TagSearchResults.Sort(result, "rank", false).Select(anime => anime.ID));
    }

    [Fact]
    public async Task ListMappings_UseCompositeForSeasonCalendarAndKeywordHydration()
    {
        await CreateBaseTablesAsync();
        MetadataRating[] ratings = [new("bangumi", 7, 3000), new("mal", 8, 100000)];
        using var handler = new StubHandler(r => r.RequestUri!.AbsolutePath == "/v1/search"
            ? Reply(new { total = 1, items = new[] { new { id = 1 } } })
            : Reply(new { total = 1, items = new[] { Work(1, null, ratings) } }));
        using var client = Client(handler);
        var source = Source(client);
        var season = Assert.Single(await source.GetAnimeBySeasonAsync(2026, Season.Spring, default));
        var calendar = Assert.Single(await source.GetCurrentBroadcastScheduleAsync(default));
        var keyword = Assert.Single((await source.SearchByKeywordAsync("测试", 0, default)).Results);
        foreach (var anime in new[] { season, calendar, keyword })
            Assert.InRange(Math.Abs(anime.Score!.Value - 7.204), 0, 0.001);
    }

    [Fact]
    public async Task DetailRatingText_UsesSameResponseAsAnimeAndSuppressesOldServiceAniList()
    {
        await CreateBaseTablesAsync();
        using var handler = new StubHandler(_ => Reply(new { data = Work(1, null,
            [new("bangumi", 7.5, 9114), new("anilist", 100, 100000)]) }));
        using var client = Client(handler);
        var source = Source(client);
        var anime = Assert.IsType<Anime>(await source.GetAnimeDetailAsync(1, default));
        var text = await source.GetRatingTextAsync(1, default);
        Assert.StartsWith($"综合评分　{anime.Score:F1}", text);
        Assert.Contains("Bangumi　7.5 分（9,114 人评分）", text);
        Assert.DoesNotContain("AniList", text);
        Assert.Single(handler.GetPaths);
    }

    [Fact]
    public async Task DetailViewModel_DoesNotApplyOldRatingTextAfterSelectionChanges()
    {
        await CreateBaseTablesAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new StubHandler(async (r, _) =>
        {
            var id = int.Parse(r.RequestUri!.Segments.Last());
            if (id == 1)
            {
                started.SetResult();
                await release.Task;
            }
            return Reply(new { data = Work(id, null, [new("bangumi", id == 1 ? 5 : 8, 500)]) });
        });
        using var client = Client(handler);
        var source = Source(client);
        var model = new AnimeDetailViewModel(source, new TrackingService(DbFactory), source);
        var first = model.LoadDetailCommand.ExecuteAsync(1);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await model.LoadDetailCommand.ExecuteAsync(2);
            Assert.Equal(2, model.AnimeDetail!.ID);
            Assert.Contains("Bangumi　8.0", model.RatingText);
        }
        finally { release.TrySetResult(); }
        await first;
        Assert.Equal(2, model.AnimeDetail!.ID);
        Assert.DoesNotContain("Bangumi　5.0", model.RatingText);
    }
}
