using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Exceptions;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace AniMeido.Tests;

public sealed partial class MetadataDataSourceTests : DbTestBase
{
    private const string Version1 = "20261019.2";
    private const string Version2 = "20261020.1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string DetailJson = """
        {"dataVersion":"ignored-body-version","data":{
          "id":42,"format":"tv","sourceType":"manga","firstDate":"2026-05-02","broadcastWeekday":6,
          "names":[{"kind":"main","lang":"ja","text":"原名"},{"kind":"main","lang":"zh-Hans","text":"中文名"},
                   {"kind":"main","lang":"en","text":"English"},{"kind":"alias","lang":null,"text":"别名"},
                   {"kind":"alias","lang":null,"text":"别名"},{"kind":"original","lang":null,"text":"不作为别名"}],
          "summaries":[{"lang":"en","text":"English summary"},{"lang":"ja","text":"日本語"},{"lang":"zh-Hans","text":"中文简介"}],
          "tags":["sci-fi"],"countries":["JP"],
          "ratings":[{"platform":"mal","score":9.9,"votes":900},{"platform":"bangumi","score":7.8,"votes":100}],
          "staff":[
            {"role":"production","participants":[{"id":11,"mainNames":{"zh-Hans":"製作方"}}]},
            {"role":"original-creator","participants":[{"id":12,"mainNames":{"ja":"原作者"}}]},
            {"role":"planning","participants":[{"id":13,"mainNames":{"en":"Planner"}}]},
            {"role":"animation-production","participants":[{"id":14,"mainNames":{"ja":"动画公司"}}]},
            {"role":"distribution","participants":[{"id":15,"mainNames":{"ja":"发行方"}},{"id":11,"mainNames":{"ja":"重复"}}]},
            {"role":"director","participants":[{"id":16,"mainNames":{"ja":"不应当作制作方"}}]}],
          "characters":[
            {"character":{"id":102,"mainNames":{"ja":"配角"}},"role":"supporting","cv":[{"id":202,"mainNames":{"ja":"声优乙"}}]},
            {"character":{"id":101,"mainNames":{"ja":"原角色名","zh-Hans":"主角"}},"role":"main",
             "cv":[{"id":201,"mainNames":{"ja":"原声优名","zh-Hans":"声优甲"}},{"id":202,"mainNames":{"ja":"声优乙"}}]}]
        }}
        """;

    [Fact]
    public void Registration_UsesMetadataServiceOnlyAndCurrentVersion()
    {
        var services = new ServiceCollection();
        services.AddMetadataService();
        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(MetadataServiceSettings.ClientName);
        Assert.Equal(new Uri(MetadataServiceSettings.QueryBaseUrl), client.BaseAddress);
        Assert.Equal(TimeSpan.FromSeconds(10), client.Timeout);
        var version = typeof(MetadataDataSource).Assembly.GetName().Version!.ToString(3);
        Assert.Contains($"AniMeido/{version}", client.DefaultRequestHeaders.GetValues("User-Agent").Single());
        Assert.Contains(services, d => d.ServiceType == typeof(IAnimeDataSource));
    }

    [Fact]
    public async Task Detail_MapsEveryFieldAndSharesOneResponseWithRelatedMethods()
    {
        await CreateBaseTablesAsync();
        using var handler = new StubHandler(_ => Raw(DetailJson));
        using var client = Client(handler);
        var source = Source(client);
        var anime = Assert.IsType<Anime>(await source.GetAnimeDetailAsync(42, default));
        Assert.Equal(42, anime.ID);
        Assert.Equal("中文名", anime.Title);
        Assert.Equal(new[] { "原名", "English", "别名" }, anime.AlternateTitles);
        Assert.Equal("中文简介", anime.Description);
        Assert.Equal(new DateOnly(2026, 5, 2), anime.AirDate);
        Assert.Equal(2026, anime.SeasonYear);
        Assert.Equal(4, anime.SeasonMonth);
        Assert.Equal(6, anime.Weekday);
        Assert.Equal(CompositeRating.Calculate([new("mal", 9.9, 900), new("bangumi", 7.8, 100)], "2026-05-02", new(2026, 10, 19))?.Score, anime.Score);
        Assert.Equal(AnimeMediaFormat.Television, anime.MediaFormat);
        Assert.Equal(Image("work/42/cover", "large"), anime.CoverURL);
        Assert.Equal("製作方、原作者、Planner、动画公司、发行方", anime.Studio);
        var studios = await source.GetStudioAsync(42, default);
        Assert.Equal(new[] { 11, 12, 13, 14, 15 }, studios.Select(s => s.ID));
        Assert.Equal(Image("participant/11/avatar", "large"), studios[0].CoverURL);
        Assert.Equal(new[] { 201, 202 }, anime.CVs.Select(v => v.VoiceActorId));
        var cvs = await source.GetCVsAsync(42, default);
        Assert.Equal("声优甲", cvs[0].Name);
        Assert.Equal(Image("participant/201/avatar", "large"), cvs[0].CoverURL);
        var characters = await source.GetCharacterRolesAsync(42, default);
        Assert.Equal(new[] { 101, 102 }, characters.Select(c => c.CharacterId));
        Assert.Equal("主角", characters[0].CharacterName);
        Assert.Null(characters[0].CharacterSummary);
        Assert.Equal(Image("character/101/character", "large"), characters[0].CharacterImage);
        Assert.Equal(new[] { 201, 202 }, characters[0].Actors.Select(v => v.VoiceActorId));
        Assert.Single(handler.GetPaths);
    }

    [Theory]
    [InlineData("tv", AnimeMediaFormat.Television)]
    [InlineData("ova", AnimeMediaFormat.Ova)]
    [InlineData("movie", AnimeMediaFormat.Movie)]
    [InlineData("web", AnimeMediaFormat.Ona)]
    [InlineData("short", AnimeMediaFormat.Unknown)]
    [InlineData(null, AnimeMediaFormat.Unknown)]
    public void Format_UsesMetadataCodes(string? format, AnimeMediaFormat expected) =>
        Assert.Equal(expected, MetadataDataSource.MapMediaFormat(format));

    [Theory]
    [InlineData("2026-05", 2026, 4)]
    [InlineData("2026", 0, 0)]
    [InlineData(null, 0, 0)]
    public async Task Detail_PartialDateHasNoAirDateAndMissingBangumiScoreStaysNull(string? date, int year, int month)
    {
        await CreateBaseTablesAsync();
        using var handler = new StubHandler(_ => Reply(new { data = Work(1, date) }));
        using var client = Client(handler);
        var anime = Assert.IsType<Anime>(await Source(client).GetAnimeDetailAsync(1, default));
        Assert.Null(anime.AirDate);
        Assert.Null(anime.Score);
        Assert.Equal(year, anime.SeasonYear);
        Assert.Equal(month, anime.SeasonMonth);
        Assert.Equal("作品1", anime.Title); // 没有中文时使用日文 main。
        Assert.Equal("", anime.Description);
    }

    [Theory]
    [InlineData("ja", "ja", "日本語")]
    [InlineData("en", "en", "English")]
    public async Task Detail_FallsBackToAvailableMainNameAndSummary(string language, string summaryLanguage, string text)
    {
        await CreateBaseTablesAsync();
        using var handler = new StubHandler(_ => Reply(new { data = new
        {
            id = 1, names = new[] { new { kind = "main", lang = language, text } },
            summaries = new[] { new { lang = summaryLanguage, text } },
        } }));
        using var client = Client(handler);
        var anime = Assert.IsType<Anime>(await Source(client).GetAnimeDetailAsync(1, default));
        Assert.Equal(text, anime.Title);
        Assert.Equal(text, anime.Description);
    }

    [Fact]
    public async Task Season_LoadsAllPagesAndDoesNotReadBangumiCache()
    {
        await CreateBaseTablesAsync();
        var cache = new CacheService(DbFactory);
        await cache.SetCacheAsync("season:v4:2026:Spring", "legacy-bangumi-cache", TimeSpan.FromDays(1));
        using var handler = new StubHandler(request =>
        {
            Assert.Equal("2026-04", Query(request, "season"));
            Assert.Equal("100", Query(request, "limit"));
            var offset = int.Parse(Query(request, "offset"));
            return Reply(new { total = 105, items = Enumerable.Range(offset + 1, Math.Min(100, 105 - offset)).Select(id => Work(id)) });
        });
        using var client = Client(handler);
        var result = await Source(client, cache).GetAnimeBySeasonAsync(2026, Season.Spring, default);
        Assert.Equal(105, result.Count);
        Assert.Equal(new[] { "0", "100" }, handler.GetPaths.Select(p => Query(new Uri("https://test" + p), "offset")));
        Assert.All(result, anime => { Assert.Null(anime.Studio); Assert.Empty(anime.CVs); });
        Assert.Equal(Image("work/1/cover", "small"), result[0].CoverURL);
        Assert.NotNull(await cache.GetCacheAsync(MetadataDataSource.GetSeasonCacheKey(2026, Season.Spring, Version1)));
        Assert.Equal("legacy-bangumi-cache", await cache.GetCacheAsync("season:v4:2026:Spring"));
    }

    [Fact]
    public async Task Calendar_UsesAiringEndpointWithoutSeasonOrWeekdayFiltering()
    {
        await CreateBaseTablesAsync();
        using var handler = new StubHandler(r =>
        {
            Assert.Equal("/v1/works/airing", r.RequestUri!.AbsolutePath);
            return Reply(new { total = 1, items = new[] { Work(10, "2026-07-01") } });
        });
        using var client = Client(handler);
        var result = await Source(client).GetCurrentBroadcastScheduleAsync(default);
        Assert.Equal(10, Assert.Single(result).ID);
    }

    [Fact]
    public async Task KeywordSearch_HydratesOnlyRequestedPageAndRestoresSearchOrder()
    {
        await CreateBaseTablesAsync();
        using var handler = new StubHandler(r =>
        {
            if (r.RequestUri!.AbsolutePath == "/v1/search")
            {
                Assert.Equal("a&中文", Query(r, "q")); Assert.Equal("work", Query(r, "type"));
                Assert.Equal("20", Query(r, "offset")); Assert.Equal("20", Query(r, "limit"));
                return Reply(new { total = 52, items = new[] { new { id = 3 }, new { id = 1 } } });
            }
            Assert.Equal("3,1", Query(r, "ids"));
            return Reply(new { total = 2, items = new[] { Work(1), Work(3) } });
        });
        using var client = Client(handler);
        var result = await Source(client).SearchByKeywordAsync("a&中文", 20, default);
        Assert.Equal(52, result.Total);
        Assert.Equal(22, result.NextOffset);
        Assert.Equal(new[] { 3, 1 }, result.Results.Select(w => w.ID));
    }

    [Theory]
    [InlineData("科幻", "tag", "sci-fi")]
    [InlineData("TV", "format", "tv")]
    [InlineData("日本", "country", "JP")]
    [InlineData("漫画改编", "sourceType", "manga")]
    public async Task TagNames_RoundTripAllFourVocabularies(string name, string parameter, string code)
    {
        await CreateBaseTablesAsync();
        using var handler = new StubHandler(r =>
        {
            var path = r.RequestUri!.AbsolutePath;
            if (path == "/v1/works/42") return Raw(DetailJson);
            if (path == "/v1/vocab") return Vocab(r);
            Assert.Equal("/v1/brief", path);
            Assert.Equal(code, Query(r, parameter));
            return Reply(new { total = 0, items = Array.Empty<object>() });
        });
        using var client = Client(handler);
        var source = Source(client);
        Assert.Equal(new[] { "科幻", "TV", "漫画改编", "日本" }, (await source.GetTagsAsync(42, default)).Select(t => t.Name));
        Assert.Empty((await source.SearchByTagAsync(name, 0, "rank", default)).Results);
        Assert.Equal(4, handler.GetPaths.Count(p => p.StartsWith("/v1/vocab")));
    }

    [Fact]
    public async Task UnknownTag_ReturnsEmptyWithoutSendingBriefQuery()
    {
        await CreateBaseTablesAsync();
        using var handler = new StubHandler(Vocab);
        using var client = Client(handler);
        var result = await Source(client).SearchByTagAsync("漫画改", 0, "rank", default);
        Assert.Empty(result.Results); Assert.Equal(0, result.Total);
        Assert.DoesNotContain(handler.GetPaths, p => p.StartsWith("/v1/brief"));
    }

    [Fact]
    public async Task TagRank_PutsCompositeBeforeUnrankedBangumiFallback()
    {
        await CreateBaseTablesAsync();
        using var handler = TagHandler(new[]
        {
            Brief(1, "2026-04-01", null, 999), Brief(2, "2026-04-02", 8, 10),
            Brief(3, "2026-04-03", 8, 100), Brief(4, "2026-04-04", 9, 1),
        });
        using var client = Client(handler);
        var result = await Source(client).SearchByTagAsync("科幻", 0, "rank", default);
        Assert.Equal(4, result.Total);
        Assert.Equal(new[] { 3, 4, 2, 1 }, result.Results.Select(a => a.ID));
    }

    [Fact]
    public async Task TagDateFilter_IsFromInclusiveToExclusiveAndRunsBeforePagination()
    {
        await CreateBaseTablesAsync();
        var briefs = Enumerable.Range(1, 25).Select(id => Brief(id, $"2026-04-{id:D2}", 8, id)).ToList();
        briefs.Add(Brief(26, "2026-03-31", 10, 1));
        briefs.Add(Brief(27, "2026-05-01", 10, 1));
        briefs.Add(Brief(28, "2026-04", 10, 1));
        using var handler = TagHandler(briefs);
        using var client = Client(handler);
        var result = await Source(client).SearchByTagAsync("科幻", 20, "date", default, "2026-04-01", "2026-05-01");
        // 只有年月的 2026-04 按 4 月 1 日计入范围，按字符串降序排在完整日期之后。
        Assert.Equal(26, result.Total);
        Assert.Equal(new[] { 5, 4, 3, 2, 1, 28 }, result.Results.Select(a => a.ID));
        Assert.Contains(handler.GetPaths, p => p.Contains("ids=5,4,3,2,1,28"));
    }

    [Theory]
    [InlineData("2026-10-05", "2026-10-05")]
    [InlineData("2026-10", "2026-10-01")]
    [InlineData("2026", "2026-01-01")]
    [InlineData("2026-1", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void StartDate_UsesFirstDayForPartialDates(string? value, string? expected) =>
        Assert.Equal(expected is null ? null : DateOnly.Parse(expected), MetadataDataSource.StartDate(value));

    [Fact]
    public async Task TagDateFilter_IncludesYearAndMonthOnlyDatesInRange()
    {
        await CreateBaseTablesAsync();
        using var handler = TagHandler([
            Brief(1, "2026", 8, 10), Brief(2, "2026-10", 8, 10), Brief(3, "2026-12-31", 8, 10),
            Brief(4, "2025-12", 8, 10), Brief(5, "2027", 8, 10), Brief(6, null, 8, 10),
        ]);
        using var client = Client(handler);
        var result = await Source(client).SearchByTagAsync("科幻", 0, "date", default, "2026-01-01", "2027-01-01");
        Assert.Equal(new[] { 3, 2, 1 }, result.Results.Select(a => a.ID));
        Assert.Equal(3, result.Total);
    }

    [Fact]
    public async Task PersonWorks_LoadsEveryPageDeduplicatesAndNamesRoles()
    {
        await CreateBaseTablesAsync();
        using var handler = new StubHandler(r =>
        {
            if (r.RequestUri!.AbsolutePath == "/v1/vocab") return Vocab(r);
            Assert.Equal("/v1/participants/7/works", r.RequestUri.AbsolutePath);
            var first = Query(r, "offset") == "0";
            return Reply(new { total = 3, items = first ? new object[]
            {
                new { work = Brief(1, "2026-04-01", 7, 100), character = new { id = 9, mainNames = new { ja = "角色" } }, role = (string?)null },
                new { work = Brief(2, "2026-03-01", null, 0), character = (object?)null, role = "director" },
            } : new object[] { new { work = Brief(1, "2026-04-01", 7, 100), character = (object?)null, role = "director" } } });
        });
        using var client = Client(handler);
        var result = await Source(client).GetPersonWorksAsync(7, default);
        Assert.Equal(new[] { 1, 2 }, result.Select(w => w.ID));
        Assert.Equal("配音、导演", result[0].Staff);
        Assert.Equal(6.33 + 100.0 / 150 * (7 - 6.33), result[0].Score);
        Assert.Null(result[1].Score);
        Assert.Equal(new DateOnly(2026, 4, 1), result[0].AirDate);
        Assert.Equal(Image("work/1/cover", "small"), result[0].CoverURL);
        Assert.Equal(2, handler.GetPaths.Count(p => p.StartsWith("/v1/participants")));
    }

    [Fact]
    public async Task Merged404_FollowsTargetOnceAndReturnsTargetPublicId()
    {
        await CreateBaseTablesAsync();
        using var handler = new StubHandler(r => r.RequestUri!.AbsolutePath == "/v1/works/1"
            ? Reply(new { code = "merged", mergedInto = 42 }, status: HttpStatusCode.NotFound) : Raw(DetailJson));
        using var client = Client(handler);
        Assert.Equal(42, (await Source(client).GetAnimeDetailAsync(1, default))!.ID);
        Assert.Equal(new[] { "/v1/works/1", "/v1/works/42" }, handler.GetPaths);
    }

    [Theory]
    [InlineData("not_found")]
    [InlineData("deactivated")]
    [InlineData("merged")]
    public async Task MissingOrInvalidMerge_ReturnsNull(string code)
    {
        await CreateBaseTablesAsync();
        using var handler = new StubHandler(_ => Reply(new { code, mergedInto = -1 }, status: HttpStatusCode.NotFound));
        using var client = Client(handler);
        Assert.Null(await Source(client).GetAnimeDetailAsync(1, default));
        Assert.Single(handler.GetPaths);
    }

    [Fact]
    public async Task MergeCycle_StopsAfterOneTargetRequest()
    {
        await CreateBaseTablesAsync();
        using var handler = new StubHandler(r => Reply(new { code = "merged", mergedInto = r.RequestUri!.AbsolutePath.EndsWith("/1") ? 2 : 1 }, status: HttpStatusCode.NotFound));
        using var client = Client(handler);
        Assert.Null(await Source(client).GetAnimeDetailAsync(1, default));
        Assert.Equal(2, handler.GetPaths.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    public async Task NonPositiveIds_DoNotSendAnyRequest(int id)
    {
        await CreateBaseTablesAsync();
        using var handler = new StubHandler(_ => throw new InvalidOperationException("Must not send"));
        using var client = Client(handler);
        var source = Source(client);
        Assert.Null(await source.GetAnimeDetailAsync(id, default));
        Assert.Empty(await source.GetStudioAsync(id, default));
        Assert.Empty(await source.GetTagsAsync(id, default));
        Assert.Empty(await source.GetCVsAsync(id, default));
        Assert.Empty(await source.GetCharacterRolesAsync(id, default));
        Assert.Empty(await source.GetPersonWorksAsync(id, default));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task DataVersion_RefreshesSeasonAndVocabularyKeysFromHeaderNotBody()
    {
        await CreateBaseTablesAsync();
        var time = new TestTime();
        var cache = new CacheService(DbFactory);
        var current = Version1;
        using var handler = new StubHandler(r => r.RequestUri!.AbsolutePath == "/v1/vocab" ? Vocab(r, current)
            : Reply(new { dataVersion = "wrong-body-version", total = 1, items = new[] { Work(current == Version1 ? 1 : 2) } }, current));
        using var client = Client(handler);
        var source = Source(client, cache, time);
        Assert.Equal(1, Assert.Single(await source.GetAnimeBySeasonAsync(2026, Season.Spring, default)).ID);
        await source.SearchByTagAsync("不匹配", 0, "rank", default);
        current = handler.Version = Version2;
        time.Now += TimeSpan.FromMinutes(6);
        Assert.Equal(2, Assert.Single(await source.GetAnimeBySeasonAsync(2026, Season.Spring, default)).ID);
        await source.SearchByTagAsync("不匹配", 0, "rank", default);
        foreach (var version in new[] { Version1, Version2 })
        {
            Assert.NotNull(await cache.GetCacheAsync(MetadataDataSource.GetSeasonCacheKey(2026, Season.Spring, version)));
            Assert.NotNull(await cache.GetCacheAsync($"metadata:vocab:tag:data:{version}"));
        }
        Assert.Equal(8, handler.GetPaths.Count(p => p.StartsWith("/v1/vocab")));
        Assert.Equal(Version2, await cache.GetCacheAsync(MetadataDataSource.VersionCacheKey));
    }

    [Fact]
    public async Task DetailFailure_ReturnsExpiredMetadataCacheButNeverHidesCallerCancellation()
    {
        await CreateBaseTablesAsync();
        var cache = new CacheService(DbFactory);
        await cache.SetCacheAsync("metadata:detail:42", DetailJson, TimeSpan.FromDays(-1));
        using var handler = new StubHandler(_ => throw new HttpRequestException("offline"));
        using var client = Client(handler);
        var source = Source(client, cache);
        Assert.Equal(42, (await source.GetAnimeDetailAsync(42, default))!.ID);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.GetAnimeDetailAsync(42, cts.Token));
        Assert.Single(handler.GetPaths);
    }

    [Fact]
    public async Task CacheMiss_ConcurrentRelatedReadsShareOneDetailRequest()
    {
        await CreateBaseTablesAsync();
        using var handler = new StubHandler(async (_, ct) => { await Task.Delay(30, ct); return Raw(DetailJson); });
        using var client = Client(handler);
        var source = Source(client);
        await Task.WhenAll(source.GetAnimeDetailAsync(42, default), source.GetStudioAsync(42, default),
            source.GetCVsAsync(42, default), source.GetCharacterRolesAsync(42, default));
        Assert.Single(handler.GetPaths);
    }

    [Fact]
    public async Task IncompleteSeason_DoesNotSaveTruncatedList()
    {
        await CreateBaseTablesAsync();
        var cache = new CacheService(DbFactory);
        using var handler = new StubHandler(r => Reply(new { total = 2, items = Query(r, "offset") == "0" ? new[] { Work(1) } : Array.Empty<object>() }));
        using var client = Client(handler);
        await Assert.ThrowsAsync<MetadataServiceException>(() => Source(client, cache).GetAnimeBySeasonAsync(2026, Season.Spring, default));
        Assert.Null(await cache.GetCacheAsync(MetadataDataSource.GetSeasonCacheKey(2026, Season.Spring, Version1)));
    }

    [Fact]
    public async Task NewVersionFailure_CanUseLastMetadataSnapshotWithoutOverwritingIt()
    {
        await CreateBaseTablesAsync();
        var time = new TestTime();
        var cache = new CacheService(DbFactory);
        var offline = false;
        using var handler = new StubHandler(_ => offline ? throw new HttpRequestException("offline")
            : Reply(new { total = 1, items = new[] { Work(1) } }));
        using var client = Client(handler);
        var source = Source(client, cache, time);
        await source.GetAnimeBySeasonAsync(2026, Season.Spring, default);
        handler.Version = Version2;
        time.Now += TimeSpan.FromMinutes(6);
        offline = true;
        Assert.Equal(1, Assert.Single(await source.GetAnimeBySeasonAsync(2026, Season.Spring, default)).ID);
        Assert.Null(await cache.GetCacheAsync(MetadataDataSource.GetSeasonCacheKey(2026, Season.Spring, Version2)));
        Assert.NotNull(await cache.GetCacheAsync(MetadataDataSource.GetSeasonCacheKey(2026, Season.Spring, Version1)));
    }

    [Fact]
    public async Task VersionChangesAfterProbe_SaveUnderResponseHeaderVersion()
    {
        await CreateBaseTablesAsync();
        var cache = new CacheService(DbFactory);
        using var handler = new StubHandler(_ => Reply(new { total = 1, items = new[] { Work(1) } }, Version2));
        using var client = Client(handler);
        await Source(client, cache).GetAnimeBySeasonAsync(2026, Season.Spring, default);
        Assert.Null(await cache.GetCacheAsync(MetadataDataSource.GetSeasonCacheKey(2026, Season.Spring, Version1)));
        Assert.NotNull(await cache.GetCacheAsync(MetadataDataSource.GetSeasonCacheKey(2026, Season.Spring, Version2)));
        Assert.Equal(MetadataDataSource.GetSeasonCacheKey(2026, Season.Spring, Version2),
            await MetadataDataSource.GetSeasonCacheKeyAsync(cache, 2026, Season.Spring));
    }

    [Fact]
    public async Task VersionChangesBetweenPages_DoNotCacheMixedRelease()
    {
        await CreateBaseTablesAsync();
        var cache = new CacheService(DbFactory);
        using var handler = new StubHandler(r => Reply(new { total = 2, items = new[] { Work(1) } },
            Query(r, "offset") == "0" ? Version1 : Version2));
        using var client = Client(handler);
        await Assert.ThrowsAsync<MetadataServiceException>(() => Source(client, cache).GetAnimeBySeasonAsync(2026, Season.Spring, default));
        Assert.Null(await cache.GetCacheAsync(MetadataDataSource.GetSeasonCacheKey(2026, Season.Spring, Version1)));
        Assert.Null(await cache.GetCacheAsync(MetadataDataSource.GetSeasonCacheKey(2026, Season.Spring, Version2)));
    }

    [Fact]
    public async Task CancelledWaiter_DoesNotCancelSharedDetailOrSendAnotherRequest()
    {
        await CreateBaseTablesAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new StubHandler(async (_, ct) =>
        {
            started.TrySetResult(); await release.Task.WaitAsync(ct); return Raw(DetailJson);
        });
        using var client = Client(handler);
        var source = Source(client);
        var first = source.GetAnimeDetailAsync(42, default);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var cts = new CancellationTokenSource();
            var waiter = source.GetStudioAsync(42, cts.Token);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        }
        finally { release.TrySetResult(); }
        Assert.Equal(42, (await first)!.ID);
        Assert.Single(handler.GetPaths);
    }

    [Fact]
    public async Task CorruptDetailCache_IsReplacedByValidResponse()
    {
        await CreateBaseTablesAsync();
        var cache = new CacheService(DbFactory);
        await cache.SetCacheAsync("metadata:detail:42", "broken-json", TimeSpan.FromDays(1));
        using var handler = new StubHandler(_ => Raw(DetailJson));
        using var client = Client(handler);
        Assert.Equal(42, (await Source(client, cache).GetAnimeDetailAsync(42, default))!.ID);
        Assert.Single(handler.GetPaths);
        Assert.Contains("dataVersion", await cache.GetCacheAsync("metadata:detail:42"));
    }

    [Fact]
    public async Task BatchMissingStatuses_AreNotMappedToFakeAnime()
    {
        await CreateBaseTablesAsync();
        using var handler = new StubHandler(r => r.RequestUri!.AbsolutePath == "/v1/search"
            ? Reply(new { total = 4, items = new[] { new { id = 1 }, new { id = 2 }, new { id = 3 }, new { id = 4 } } })
            : Reply(new { total = 4, items = new object[] { Work(1), new { id = 2, status = "merged", mergedInto = 1 },
                new { id = 3, status = "deactivated" }, new { id = 4, status = "not_found" } } }));
        using var client = Client(handler);
        var result = await Source(client).SearchByKeywordAsync("测试", 0, default);
        Assert.Equal(4, result.Total);
        Assert.Equal(4, result.NextOffset);
        Assert.Equal(1, Assert.Single(result.Results).ID);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GlobalSearch_ContinuesPastFilteredPagesWithoutRepeatingResults(bool filterFirstPage)
    {
        await RunProductionMigrationAsync();
        using var handler = KeywordPagesHandler(filterFirstPage);
        using var client = Client(handler);
        var source = new PersonalAnimeDataSource(Source(client), DbFactory);
        var tracking = new TrackingService(DbFactory);
        var vm = new GlobalSearchViewModel(source, tracking, new LocalSearchService(tracking, source), new SavedTagService(DbFactory));

        await vm.SearchAsync("测试");
        Assert.Equal(filterFirstPage ? [] : new[] { 1 }, vm.Results.Select(entry => entry.Anime.ID));
        Assert.True(vm.CanLoadMore);

        await vm.LoadMoreAsync();
        Assert.Equal(filterFirstPage ? [] : new[] { 1 }, vm.Results.Select(entry => entry.Anime.ID));
        Assert.True(vm.CanLoadMore);

        await vm.LoadMoreAsync();
        await vm.LoadMoreAsync();
        Assert.Equal(filterFirstPage ? new[] { 41 } : new[] { 1, 41 }, vm.Results.Select(entry => entry.Anime.ID));
        Assert.False(vm.CanLoadMore);
        Assert.False(vm.IsError);
        Assert.Equal(new[] { "0", "20", "40" }, handler.GetPaths.Where(p => p.StartsWith("/v1/search?", StringComparison.Ordinal))
            .Select(p => Query(new Uri("https://fixture.invalid" + p), "offset")));
    }

    [Fact]
    public async Task PendingTargetSearch_ReturnsRawNextOffsetEvenWhenWholePageIsFiltered()
    {
        await RunProductionMigrationAsync();
        using var handler = KeywordPagesHandler(filterFirstPage: false);
        using var client = Client(handler);
        var remap = new PersonalIdRemapService(DbFactory, NullLogger<PersonalIdRemapService>.Instance);
        var vm = new PendingReviewViewModel(new PendingReviewService(DbFactory, remap), remap,
            new PersonalAnimeDataSource(Source(client), DbFactory));

        var first = await vm.SearchAsync("测试", 0, default);
        Assert.Equal(1, Assert.Single(first.Results).ID);
        Assert.Equal(20, first.NextOffset);
        var middle = await vm.SearchAsync("测试", first.NextOffset, default);
        Assert.Empty(middle.Results);
        Assert.Equal(40, middle.NextOffset);
        Assert.True(middle.NextOffset < middle.Total);
        var last = await vm.SearchAsync("测试", middle.NextOffset, default);
        Assert.Equal(41, Assert.Single(last.Results).ID);
        Assert.Equal(last.Total, last.NextOffset);
        Assert.Equal(new[] { "0", "20", "40" }, handler.GetPaths.Where(p => p.StartsWith("/v1/search?", StringComparison.Ordinal))
            .Select(p => Query(new Uri("https://fixture.invalid" + p), "offset")));
    }

    [Fact]
    public async Task KeywordSearch_RejectsRawEmptyPageBeforeTotalInsteadOfLoopingAtSameOffset()
    {
        await CreateBaseTablesAsync();
        using var handler = new StubHandler(_ => Reply(new { total = 41, items = Array.Empty<object>() }));
        using var client = Client(handler);
        await Assert.ThrowsAsync<MetadataServiceException>(() => Source(client).SearchByKeywordAsync("测试", 20, default));
    }

    [Fact]
    public async Task TagPage_LoadsWholeYearRangeInRankOrderAndPreservesVotesOnSorting()
    {
        await CreateBaseTablesAsync();
        var briefs = Enumerable.Range(1, 25).Select(id => Brief(id, "2026-04-01", 8, id)).ToList();
        using var handler = TagHandler(briefs);
        using var client = Client(handler);
        var results = await TagSearchResults.LoadAsync(Source(client), "科幻", 2025, 2026, default);
        Assert.Equal(Enumerable.Range(1, 25).Reverse(), results.Select(a => a.ID));
        var sorted = TagSearchResults.Sort(results, "rank", descending: true);
        Assert.Equal(results.Select(a => a.ID), sorted.Select(a => a.ID));
        Assert.Single(handler.GetPaths, p => p.StartsWith("/v1/brief"));
        Assert.Equal(2, handler.GetPaths.Count(p => p.StartsWith("/v1/works?ids=")));
    }

    [Fact]
    public async Task TagVocabularies_VersionSwitchDuringReadRetriesWithoutMixingNames()
    {
        await CreateBaseTablesAsync();
        var version = Version1;
        using var handler = new StubHandler(r =>
        {
            if (r.RequestUri!.AbsolutePath == "/v1/works/42") return Raw(DetailJson);
            var category = Query(r, "category");
            if (category == "format") version = Version2;
            if (category == "tag" && version == Version2)
                return Reply(new { total = 1, items = new[] { new { category, code = "sci-fi", nameZh = "科幻新版" } } }, version);
            return Vocab(r, version);
        });
        using var client = Client(handler);
        var tags = await Source(client).GetTagsAsync(42, default);
        Assert.Contains(tags, t => t.Name == "科幻新版");
        Assert.DoesNotContain(tags, t => t.Name == "科幻");
        Assert.Equal(2, handler.GetPaths.Count(p => p == "/v1/vocab?category=tag"));
    }

    private MetadataDataSource Source(HttpClient client, CacheService? cache = null, TimeProvider? time = null) =>
        new(NullLogger<MetadataDataSource>.Instance, client, cache ?? new CacheService(DbFactory), time ?? new TestTime());
    private static HttpClient Client(HttpMessageHandler handler) => new(handler, disposeHandler: false)
        { BaseAddress = new Uri(MetadataServiceSettings.QueryBaseUrl) };
    private static string Image(string path, string size) => $"{MetadataServiceSettings.ImageBaseUrl}/v1/images/{path}?size={size}";
    private static object Work(int id, string? date = "2026-04-01", IReadOnlyList<MetadataRating>? ratings = null) => new
    {
        id, firstDate = date, format = "tv", names = new[] { new { kind = "main", lang = "ja", text = $"作品{id}" } },
        ratings = ratings ?? [],
    };
    private static object Brief(int id, string? date, double? score, int votes) => new
    {
        id, firstDate = date, mainNames = new { ja = $"作品{id}" },
        ratings = new[] { new { platform = "bangumi", score, votes } },
    };
    private static HttpResponseMessage Vocab(HttpRequestMessage r) => Vocab(r, Version1);
    private static HttpResponseMessage Vocab(HttpRequestMessage r, string version)
    {
        var category = Query(r, "category");
        var (code, name) = category switch
        {
            "tag" => ("sci-fi", "科幻"), "format" => ("tv", "TV"), "country" => ("JP", "日本"),
            "source_type" => ("manga", "漫画改编"), "staff_role" => ("director", "导演"),
            _ => throw new InvalidOperationException(category),
        };
        return Reply(new { total = 1, items = new[] { new { category, code, nameZh = name } } }, version);
    }
    private static StubHandler TagHandler(IEnumerable<object> briefs)
    {
        var items = briefs.Select(b => JsonSerializer.Deserialize<MetadataBrief>(JsonSerializer.Serialize(b, JsonOptions), JsonOptions)!).ToList();
        return new StubHandler(r =>
        {
            if (r.RequestUri!.AbsolutePath == "/v1/vocab") return Vocab(r);
            if (r.RequestUri.AbsolutePath == "/v1/brief") return Reply(new { total = items.Count, items });
            var ids = Query(r, "ids").Split(',').Select(int.Parse).Reverse();
            return Reply(new { total = ids.Count(), items = ids.Select(id =>
            {
                var brief = items.Single(i => i.Id == id);
                return Work(id, brief.FirstDate, brief.Ratings);
            }) });
        });
    }

    private static StubHandler KeywordPagesHandler(bool filterFirstPage) => new(r =>
    {
        if (r.RequestUri!.AbsolutePath == "/v1/search")
        {
            var offset = int.Parse(Query(r, "offset"));
            return Reply(new { total = 41, items = Enumerable.Range(1, 41).Skip(offset).Take(20).Select(id => new { id }) });
        }
        Assert.Equal("/v1/works", r.RequestUri.AbsolutePath);
        var ids = Query(r, "ids").Split(',').Select(int.Parse).ToList();
        return Reply(new
        {
            total = ids.Count,
            items = ids.Select(id => id == 41 || (id == 1 && !filterFirstPage) ? Work(id)
                : new { id, status = (id % 3) switch { 0 => "merged", 1 => "deactivated", _ => "not_found" } }),
        });
    });

    private static string Query(HttpRequestMessage r, string key) => Query(r.RequestUri!, key);
    private static string Query(Uri uri, string key) => uri.Query.TrimStart('?').Split('&')
        .Select(p => p.Split('=', 2)).Where(p => p.Length == 2 && p[0] == key).Select(p => Uri.UnescapeDataString(p[1])).Single();
    private static HttpResponseMessage Reply(object body, string version = Version1, HttpStatusCode status = HttpStatusCode.OK) =>
        Raw(JsonSerializer.Serialize(body, JsonOptions), version, status);
    private static HttpResponseMessage Raw(string body, string version = Version1, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        response.Headers.Add("X-Data-Version", version);
        return response;
    }
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;
        public string Version { get; set; } = Version1;
        public ConcurrentQueue<(string Method, string Path)> Requests { get; } = new();
        public List<string> GetPaths => Requests.Where(r => r.Method == "GET").Select(r => r.Path).ToList();
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : this((r, _) => Task.FromResult(respond(r))) { }
        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue((request.Method.Method, request.RequestUri!.PathAndQuery));
            return request.Method == HttpMethod.Head ? Task.FromResult(Raw("", Version)) : _respond(request, cancellationToken);
        }
    }
    private sealed class TestTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 19, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
