using System.Net;
using System.Net.Http.Headers;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Exceptions;
using AniMeido.Plugin.Base.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AniMeido.Tests;

public sealed class MetadataRateLimitTests : IAsyncLifetime
{
    private readonly PersonalDatabaseFixture _database = new();
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);
    private const string Version = "20261030.1";
    public Task InitializeAsync() => _database.InitializeAsync();
    public Task DisposeAsync() => _database.DisposeAsync();

    [Fact]
    public async Task HeaderlessRateLimitKeepsItsReasonAndTheNextSeasonLoadCanRecover()
    {
        var time = new ServiceTestTime();
        var limited = true;
        using var http = Client(request => request.Method == HttpMethod.Head ? Json("")
            : limited ? Limited(60) : Json("{\"items\":[],\"total\":0}"));
        var cache = new CacheService(_database.Factory);
        var source = new MetadataDataSource(NullLogger<MetadataDataSource>.Instance, http, cache, time);
        var error = await Assert.ThrowsAsync<MetadataServiceException>(() => source.GetAnimeBySeasonAsync(2026, Season.Fall, default));
        Assert.True(error.IsRateLimited);
        Assert.Null(await cache.GetCacheAsync(MetadataDataSource.GetSeasonCacheKey(2026, Season.Fall, Version)));
        limited = false;
        time.Advance(TimeSpan.FromSeconds(60));
        Assert.Empty(await source.GetAnimeBySeasonAsync(2026, Season.Fall, default));
    }

    [Fact]
    public async Task SeasonPagesUseOneWaitBudgetAndNeverSavePartialResults()
    {
        var time = new ServiceTestTime();
        var firstPage = 0;
        var secondPage = 0;
        using var http = Client(request =>
        {
            if (request.Method == HttpMethod.Head) return Json("");
            if (request.RequestUri!.Query.Contains("offset=0", StringComparison.Ordinal))
                return ++firstPage == 1 ? Limited(20) : Json("{\"items\":[{\"id\":42}],\"total\":2}");
            secondPage++;
            return Limited(15);
        });
        var cache = new CacheService(_database.Factory);
        var source = new MetadataDataSource(NullLogger<MetadataDataSource>.Instance, http, cache, time);
        var loading = source.GetAnimeBySeasonAsync(2026, Season.Fall, default);
        try
        {
            await time.WaitForDelayAsync(TimeSpan.FromSeconds(20)).WaitAsync(Budget);
            time.Advance(TimeSpan.FromSeconds(20));
            var error = await Assert.ThrowsAsync<MetadataServiceException>(() => loading.WaitAsync(Budget));
            Assert.True(error.IsRateLimited);
            Assert.Equal(2, firstPage);
            Assert.Equal(1, secondPage);
            Assert.Null(await cache.GetCacheAsync(MetadataDataSource.GetSeasonCacheKey(2026, Season.Fall, Version)));
        }
        finally { time.Advance(TimeSpan.FromDays(1)); await ((Task)loading).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext); }
    }

    [Fact]
    public async Task RateLimitedDetailUsesTheExistingExpiredCache()
    {
        using var http = Client(_ => Limited(60));
        var cache = new CacheService(_database.Factory);
        await cache.SetCacheAsync("metadata:detail:42", "{\"dataVersion\":\"20261030.1\",\"data\":{\"id\":42,\"names\":[{\"kind\":\"main\",\"lang\":\"zh-Hans\",\"text\":\"cached\"}]}}", TimeSpan.FromSeconds(-1));
        var logger = new ServiceTestLogger<MetadataDataSource>();
        var source = new MetadataDataSource(logger, http, cache);
        Assert.Equal(42, (await source.GetAnimeDetailAsync(42, default))!.ID);
        Assert.Equal(1, logger.Count(Microsoft.Extensions.Logging.LogLevel.Warning));
        Assert.Equal(0, logger.Count(Microsoft.Extensions.Logging.LogLevel.Error));
    }

    [Fact]
    public async Task RateLimitedSecondPageRetriesOnlyThatPageAndSavesTheCompleteSeason()
    {
        var time = new ServiceTestTime();
        var firstPage = 0;
        var secondPage = 0;
        using var http = Client(request =>
        {
            if (request.Method == HttpMethod.Head) return Json("");
            if (request.RequestUri!.Query.Contains("offset=0", StringComparison.Ordinal))
            {
                firstPage++;
                return Json("{\"items\":[{\"id\":42}],\"total\":2}");
            }
            return ++secondPage == 1 ? Limited(20) : Json("{\"items\":[{\"id\":43}],\"total\":2}");
        });
        var cache = new CacheService(_database.Factory);
        var source = new MetadataDataSource(NullLogger<MetadataDataSource>.Instance, http, cache, time);
        var loading = source.GetAnimeBySeasonAsync(2026, Season.Fall, default);
        try
        {
            await time.WaitForDelayAsync(TimeSpan.FromSeconds(20)).WaitAsync(Budget);
            Assert.Null(await cache.GetCacheAsync(MetadataDataSource.GetSeasonCacheKey(2026, Season.Fall, Version)));
            time.Advance(TimeSpan.FromSeconds(20));
            Assert.Equal(new[] { 42, 43 }, (await loading.WaitAsync(Budget)).Select(anime => anime.ID));
            Assert.NotNull(await cache.GetCacheAsync(MetadataDataSource.GetSeasonCacheKey(2026, Season.Fall, Version)));
            Assert.Equal(new[] { 42, 43 }, (await source.GetAnimeBySeasonAsync(2026, Season.Fall, default)).Select(anime => anime.ID));
            Assert.Equal(1, firstPage);
            Assert.Equal(2, secondPage);
        }
        finally
        {
            time.Advance(TimeSpan.FromDays(1));
            await ((Task)loading).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond)
        => new(new Handler(respond)) { BaseAddress = new Uri("https://api.example.test") };
    private static HttpResponseMessage Json(string content)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content) };
        response.Headers.Add("X-Data-Version", Version);
        return response;
    }
    private static HttpResponseMessage Limited(int seconds) => new(HttpStatusCode.TooManyRequests)
    {
        Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds)) },
    };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
