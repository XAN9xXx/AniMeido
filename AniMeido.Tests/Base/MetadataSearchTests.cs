using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.Exceptions;
using AniMeido.Plugin.Base.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;

namespace AniMeido.Tests;

public sealed partial class MetadataDataSourceTests
{
    [Fact]
    public async Task CompleteSearchKeepsRawRatingsAndRecalculatesAcrossShanghaiMidnightOffline()
    {
        await CreateBaseTablesAsync();
        var clock = new TestTime { Now = new(2026, 10, 19, 15, 59, 0, TimeSpan.Zero) };
        var online = true;
        using var handler = new StubHandler(request =>
        {
            if (!online) throw new HttpRequestException("offline");
            if (request.RequestUri!.AbsolutePath == "/v1/version")
                return Reply(new { data = new { search = new { searchVersion = "lucene-1", completeWorkSort = true } } });
            Assert.Equal("/v1/search/brief", request.RequestUri.AbsolutePath);
            return Reply(new { searchVersion = "lucene-1", asOf = "2026-10-19", complete = true, total = 2, items = new[]
            {
                new { id = 1, mainNames = new { ja = "原名" }, matchedName = "别名", matchedNameKind = "alias", relevanceOrder = 0,
                    firstDate = "2020", format = "tv", ratings = new[] { new MetadataRating("anilist", 90, 1000, "2026-10-04"), new MetadataRating("bangumi", 6, 100) } },
                new { id = 2, mainNames = new { ja = "第二部" }, matchedName = "第二部", matchedNameKind = "main", relevanceOrder = 1,
                    firstDate = "2020", format = "tv", ratings = new[] { new MetadataRating("bangumi", 8, 500) } }
            } });
        });
        using var client = Client(handler);
        var source = Source(client, time: clock);
        var first = await source.SearchPageAsync("query", 0, null, true, false, null, default);
        Assert.NotNull(first.Items[0].Score);
        Assert.Equal("别名", first.Raw.Items[0].MatchedName);
        Assert.Equal(new DateOnly(2020, 1, 1), first.Items[0].AirDate);
        clock.Now += TimeSpan.FromMinutes(2); online = false;
        var next = await source.SearchPageAsync("query", 0, null, true, false, null, default);
        Assert.True(next.Raw.IsOffline);
        Assert.Null(next.Items[0].Score);
        var low = await source.SearchPageAsync("query", 0, null, true, true, null, default);
        Assert.Equal(6, low.Items[0].Score);
        Assert.Equal(new[] { 1, 2 }, low.Raw.Items.Select(i => i.Id));
    }

    [Theory]
    [InlineData("search_context_changed", HttpStatusCode.Conflict)]
    [InlineData("full_sort_unavailable", HttpStatusCode.UnprocessableEntity)]
    public async Task SearchContractErrorsDoNotFallBackToOldCompleteResults(string code, HttpStatusCode status)
    {
        await CreateBaseTablesAsync();
        using var handler = new StubHandler(r => r.RequestUri!.AbsolutePath == "/v1/version"
            ? Reply(new { data = new { search = new { searchVersion = "lucene-1", completeWorkSort = true } } })
            : Reply(new { code }, status: status));
        using var client = Client(handler);
        var error = await Assert.ThrowsAsync<MetadataServiceException>(() => Source(client).SearchPageAsync("q", 0, null, true, false, null, default));
        Assert.Equal(code, error.Code);
    }

    [Fact]
    public async Task KeywordScoreSortUsesWholeSetAndRetainsLocalMatches()
    {
        await RunProductionMigrationAsync();
        using var handler = new StubHandler(r =>
        {
            if (r.RequestUri!.AbsolutePath == "/v1/version") return Reply(new { data = new { search = new { searchVersion = "lucene-1", completeWorkSort = true } } });
            if (r.RequestUri.AbsolutePath == "/v1/search")
                return Reply(new { searchVersion = "lucene-1", asOf = "2026-10-19", total = 25, items = Enumerable.Range(1, 20).Select(id => new { id, matchedName = "命中" + id }) });
            if (r.RequestUri.AbsolutePath == "/v1/search/brief")
                return Reply(new { searchVersion = "lucene-1", asOf = "2026-10-19", complete = true, total = 25,
                    items = Enumerable.Range(1, 25).Select(id => new { id, mainNames = new { ja = "作品" + id }, matchedName = "命中" + id,
                        relevanceOrder = id - 1, firstDate = "2020-01-01", format = "tv", ratings = new[] { new MetadataRating("bangumi", id / 3.0, id == 24 ? 100 : 500) } }) });
            return Reply(new { total = 20, items = Enumerable.Range(1, 20).Select(id => Work(id)) });
        });
        using var client = Client(handler);
        var source = Source(client);
        var tracking = new TrackingService(DbFactory);
        var model = new GlobalSearchViewModel(source, tracking, FeatureState,
            new LocalSearchService(tracking, source, NullLogger<LocalSearchService>.Instance),
            new SavedTagService(DbFactory), NullLogger.Instance);
        await model.SearchAsync("query");
        Assert.Equal(1, model.Results[0].Anime.ID);
        await model.SetSortAsync(SearchSortKey.Score);
        Assert.Equal(25, model.Results[0].Anime.ID);
        Assert.DoesNotContain(model.Results, i => i.Anime.ID == 24);
        await model.SetIncludeLowVotesAsync(true);
        Assert.Equal(24, model.Results[1].Anime.ID);
        Assert.Equal("命中24", model.Results[1].MatchedName);
        Assert.Single(handler.GetPaths, p => p.StartsWith("/v1/search/brief", StringComparison.Ordinal));
        await model.LoadMoreAsync();
        Assert.Equal(25, model.Results.Count);
        Assert.False(model.CanLoadMore);
        model.CancelPendingSearch();
        await model.ResumePendingSearchAsync();
        Assert.Equal(25, model.Results.Count);
        Assert.Equal(SearchSortKey.Score, model.SortKey);
        Assert.True(model.IncludeLowVotes);
    }

    [Fact]
    public void DateSortKeepsUnknownLastAndEqualDateIdsAscendingInBothDirections()
    {
        List<Anime> items = [new(3, "三", null, [], new(2020, 1, 1), null, "", 2020, 1),
            new(1, "一", null, [], new(2020, 1, 1), null, "", 2020, 1), new(2, "二", null, [], null, null, "", 0, 0)];
        Assert.Equal(new[] { 1, 3, 2 }, TagSearchResults.Sort(items, "date", true).Select(a => a.ID));
        Assert.Equal(new[] { 1, 3, 2 }, TagSearchResults.Sort(items, "date", false).Select(a => a.ID));
    }

    [Fact]
    public async Task OlderServerReceivesOnlySupportedPagingParameters()
    {
        await CreateBaseTablesAsync();
        using var handler = new StubHandler(r =>
        {
            if (r.RequestUri!.AbsolutePath == "/v1/version") return Reply(new { data = new { apiVersion = "v1" } });
            if (r.RequestUri.AbsolutePath == "/v1/search")
            {
                Assert.DoesNotContain("expected", r.RequestUri.Query);
                Assert.DoesNotContain("format=", r.RequestUri.Query);
                var id = Query(r, "offset") == "0" ? 1 : 2;
                return Reply(new { total = 2, items = new[] { new { id } } });
            }
            return Reply(new { total = 2, items = new[] { Work(1), Work(2) } });
        });
        using var client = Client(handler);
        var source = Source(client);
        var first = await source.SearchPageAsync("query", 0, null, false, false, null, default);
        var second = await source.SearchPageAsync("query", 1, null, false, false, first.Raw.Context, default);
        Assert.Equal(2, Assert.Single(second.Items).ID);
        var error = await Assert.ThrowsAsync<MetadataServiceException>(() => source.SearchPageAsync("query", 0, "movie", false, false, null, default));
        Assert.Equal("full_sort_unavailable", error.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OlderCompleteSortSuccessOrFailureCannotReplaceANewKeyword(bool fail)
    {
        await RunProductionMigrationAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new StubHandler(async (r, _) =>
        {
            if (r.RequestUri!.AbsolutePath == "/v1/version") return Reply(new { data = new { search = new { searchVersion = "lucene-1", completeWorkSort = true } } });
            if (r.RequestUri.AbsolutePath == "/v1/search/brief") { entered.TrySetResult(); return await finish.Task; }
            if (r.RequestUri.AbsolutePath == "/v1/search") return Reply(new { searchVersion = "lucene-1", asOf = "2026-10-19", total = 1,
                items = new[] { new { id = Query(r, "q") == "old" ? 1 : 2 } } });
            return Reply(new { total = 2, items = new[] { Work(1), Work(2) } });
        });
        using var client = Client(handler);
        var vm = CreateFinalBoundarySearch(Source(client), new MetadataLogCapture());
        await vm.SearchAsync("old");
        var pending = vm.SetSortAsync(SearchSortKey.Score);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await vm.SearchAsync("new");
            if (fail) finish.TrySetException(new HttpRequestException("late failure"));
            else finish.TrySetResult(Reply(new { complete = true, total = 0, items = Array.Empty<object>() }));
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, Assert.Single(vm.Results).Anime.ID);
            Assert.Equal(SearchSortKey.Match, vm.SortKey); Assert.False(vm.IsError);
        }
        finally { finish.TrySetCanceled(); await pending.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SearchRuleChangeRestartsOnceAndKeepsOldViewIfItChangesAgain(bool changesAgain)
    {
        await RunProductionMigrationAsync();
        var firstPageCalls = 0;
        using var handler = new StubHandler(r =>
        {
            if (r.RequestUri!.AbsolutePath == "/v1/version") return Reply(new { data = new { search = new { searchVersion = "lucene-1", completeWorkSort = true } } });
            if (r.RequestUri.AbsolutePath == "/v1/search")
            {
                if (Query(r, "offset") != "0") return Reply(new { code = "search_context_changed" }, status: HttpStatusCode.Conflict);
                var next = ++firstPageCalls > 1;
                if (next && changesAgain) return Reply(new { code = "search_context_changed" }, status: HttpStatusCode.Conflict);
                return Reply(new { searchVersion = next ? "lucene-2" : "lucene-1", asOf = "2026-10-19", total = next ? 1 : 2,
                    items = new[] { new { id = next ? 2 : 1 } } });
            }
            return Reply(new { total = 2, items = new[] { Work(1), Work(2) } });
        });
        using var client = Client(handler);
        var vm = CreateFinalBoundarySearch(Source(client), new MetadataLogCapture());
        await vm.SearchAsync("query");
        await vm.LoadMoreAsync();
        Assert.Equal(2, firstPageCalls);
        Assert.Equal(changesAgain ? 1 : 2, Assert.Single(vm.Results).Anime.ID);
        Assert.Equal(changesAgain, vm.CanLoadMore);
        Assert.Equal(changesAgain, vm.IsError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitialSearchAndFormatChangeRetryOnceWhenDetailsSwitchVersion(bool changeFormat)
    {
        await RunProductionMigrationAsync();
        var trigger = !changeFormat;
        var searchCalls = 0;
        var headerVersion = Version1;
        using var handler = new StubHandler(r =>
        {
            if (r.RequestUri!.AbsolutePath == "/v1/version") return Reply(new { data = new { search = new { searchVersion = "lucene-1", completeWorkSort = true } } }, headerVersion);
            if (r.RequestUri.AbsolutePath == "/v1/search")
            {
                searchCalls++;
                var version = headerVersion;
                var id = trigger && changeFormat ? 3 : version == Version1 ? 1 : 2;
                if (trigger) { trigger = false; headerVersion = Version2; }
                return Reply(new { searchVersion = "lucene-1", asOf = "2026-10-19", total = 1,
                    items = new[] { new { id } } }, version);
            }
            return Reply(new { total = 3, items = new[] { Work(1), Work(2), Work(3) } }, headerVersion);
        });
        using var client = Client(handler);
        var vm = CreateFinalBoundarySearch(Source(client), new MetadataLogCapture());
        await vm.SearchAsync("query");
        if (changeFormat)
        {
            trigger = true;
            // 新查询取不同 ID，避免已缓存的详情遮住本次版本切换。
            await vm.SelectFormatAsync(AnimeMediaFormat.Television);
        }
        Assert.False(vm.IsError);
        Assert.Equal(changeFormat ? 3 : 2, searchCalls);
        Assert.Equal(2, Assert.Single(vm.Results).Anime.ID);
    }

    [Fact]
    public async Task FailedLowVoteToggleKeepsBothPreviousOptionAndPreviousResults()
    {
        await RunProductionMigrationAsync();
        var reject = false;
        var clock = new TestTime();
        using var handler = new StubHandler(r =>
        {
            if (r.RequestUri!.AbsolutePath == "/v1/version") return Reply(new { data = new { search = new { searchVersion = "lucene-1", completeWorkSort = true } } });
            if (r.RequestUri.AbsolutePath == "/v1/search") return Reply(new { searchVersion = "lucene-1", asOf = "2026-10-19", total = 1, items = new[] { new { id = 1 } } });
            if (r.RequestUri.AbsolutePath == "/v1/search/brief")
                return reject ? Reply(new { code = "full_sort_unavailable" }, status: HttpStatusCode.UnprocessableEntity)
                    : Reply(new { searchVersion = "lucene-1", asOf = "2026-10-19", complete = true, total = 1, items = new[]
                    { new { id = 1, mainNames = new { ja = "低票作品" }, firstDate = "2020", format = "tv", ratings = new[] { new MetadataRating("bangumi", 8, 100) } } } });
            return Reply(new { total = 1, items = new[] { Work(1) } });
        });
        using var client = Client(handler);
        var vm = CreateFinalBoundarySearch(Source(client, time: clock), new MetadataLogCapture());
        await vm.SearchAsync("query");
        await vm.SetSortAsync(SearchSortKey.Score);
        await vm.SetIncludeLowVotesAsync(true);
        Assert.Equal(8, Assert.Single(vm.Results).Anime.Score);
        clock.Now += TimeSpan.FromDays(1);
        reject = true;
        await vm.SetIncludeLowVotesAsync(false);
        Assert.True(vm.IsError);
        Assert.True(vm.IncludeLowVotes);
        Assert.Equal(8, Assert.Single(vm.Results).Anime.Score);
    }
}
