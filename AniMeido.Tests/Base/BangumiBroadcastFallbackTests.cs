using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Services;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text;
using System.Text.Json;

namespace AniMeido.Tests;

public sealed class BangumiBroadcastFallbackTests : DbTestBase
{
    [Fact]
    public async Task CurrentBroadcastSchedule_EmptySeasonInArchive_RetriesOnlineAndCachesResult()
    {
        await CreateBaseTablesAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var archiveHandler = new CalendarHandler(101, PreviousSeasonDate(today));
        var fallbackHandler = new CalendarHandler(202, today);
        using var archive = CreateHttpClient(archiveHandler, "https://archive.example.test");
        using var fallback = CreateHttpClient(fallbackHandler, "https://fallback.example.test");
        var dataSource = CreateDataSource(archive, fallback, new CacheService(DbFactory));

        var result = await dataSource.GetCurrentBroadcastScheduleAsync(CancellationToken.None);
        var cached = await dataSource.GetCurrentBroadcastScheduleAsync(CancellationToken.None);

        Assert.Equal(202, Assert.Single(result).ID);
        Assert.Equal(202, Assert.Single(cached).ID);
        Assert.Equal(1, archiveHandler.RequestCount);
        Assert.Equal(1, fallbackHandler.RequestCount);
        Assert.Equal(1L, await CountBroadcastCachesAsync());
    }

    [Fact]
    public async Task CurrentBroadcastSchedule_CurrentSeasonInArchive_DoesNotRequestOnline()
    {
        await CreateBaseTablesAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var archiveHandler = new CalendarHandler(101, today);
        var fallbackHandler = new CalendarHandler(202, today);
        using var archive = CreateHttpClient(archiveHandler, "https://archive.example.test");
        using var fallback = CreateHttpClient(fallbackHandler, "https://fallback.example.test");
        var dataSource = CreateDataSource(archive, fallback, new CacheService(DbFactory));

        var result = await dataSource.GetCurrentBroadcastScheduleAsync(CancellationToken.None);
        var cached = await dataSource.GetCurrentBroadcastScheduleAsync(CancellationToken.None);

        Assert.Equal(101, Assert.Single(result).ID);
        Assert.Equal(101, Assert.Single(cached).ID);
        Assert.Equal(1, archiveHandler.RequestCount);
        Assert.Equal(0, fallbackHandler.RequestCount);
        Assert.Equal(1L, await CountBroadcastCachesAsync());
    }

    [Fact]
    public async Task CurrentBroadcastSchedule_NoCurrentSeasonInEitherSource_DoesNotCacheEmptyResult()
    {
        await CreateBaseTablesAsync();
        var previousSeason = PreviousSeasonDate(DateOnly.FromDateTime(DateTime.Today));
        var archiveHandler = new CalendarHandler(101, previousSeason);
        var fallbackHandler = new CalendarHandler(202, previousSeason);
        using var archive = CreateHttpClient(archiveHandler, "https://archive.example.test");
        using var fallback = CreateHttpClient(fallbackHandler, "https://fallback.example.test");
        var dataSource = CreateDataSource(archive, fallback, new CacheService(DbFactory));

        Assert.Empty(await dataSource.GetCurrentBroadcastScheduleAsync(CancellationToken.None));
        Assert.Empty(await dataSource.GetCurrentBroadcastScheduleAsync(CancellationToken.None));

        Assert.Equal(2, archiveHandler.RequestCount);
        Assert.Equal(2, fallbackHandler.RequestCount);
        Assert.Equal(0L, await CountBroadcastCachesAsync());
    }

    [Fact]
    public async Task CurrentBroadcastSchedule_IgnoresLegacyEmptyCache()
    {
        await CreateBaseTablesAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var cache = new CacheService(DbFactory);
        await cache.SetCacheAsync(
            $"broadcast:v2:{today.Year}:{SeasonHelper.FromMonth(today.Month)}",
            "[]",
            TimeSpan.FromHours(12));
        var archiveHandler = new CalendarHandler(101, today);
        var fallbackHandler = new CalendarHandler(202, today);
        using var archive = CreateHttpClient(archiveHandler, "https://archive.example.test");
        using var fallback = CreateHttpClient(fallbackHandler, "https://fallback.example.test");
        var dataSource = CreateDataSource(archive, fallback, cache);

        var result = await dataSource.GetCurrentBroadcastScheduleAsync(CancellationToken.None);

        Assert.Equal(101, Assert.Single(result).ID);
        Assert.Equal(1, archiveHandler.RequestCount);
        Assert.Equal(0, fallbackHandler.RequestCount);
        Assert.Equal(1L, await CountBroadcastCachesAsync());
    }

    private static BangumiDataSource CreateDataSource(HttpClient archive, HttpClient fallback, CacheService cache)
        => new(
            NullLogger<BangumiDataSource>.Instance,
            new BangumiApiClient(
                new NamedHttpClientFactory(archive, fallback),
                NullLogger<BangumiApiClient>.Instance,
                new FreshArchive()),
            cache);

    private async Task<long> CountBroadcastCachesAsync()
    {
        using var connection = await DbFactory.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM cache WHERE CacheKey LIKE 'broadcast:v3:%'";
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    private static DateOnly PreviousSeasonDate(DateOnly today)
        => new DateOnly(today.Year, SeasonHelper.ToMonth(SeasonHelper.FromMonth(today.Month)), 1)
            .AddDays(-1);

    private static HttpClient CreateHttpClient(HttpMessageHandler handler, string baseAddress)
        => new(handler) { BaseAddress = new Uri(baseAddress) };

    private sealed class FreshArchive : IArchiveFreshness
    {
        public bool PreferFallback => false;

        public Task EnsureCheckedAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NamedHttpClientFactory(HttpClient archive, HttpClient fallback) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => name switch
        {
            BangumiApiClient.ArchiveClientName => archive,
            BangumiApiClient.FallbackClientName => fallback,
            _ => throw new InvalidOperationException($"Unknown client: {name}"),
        };
    }

    private sealed class CalendarHandler(int animeId, DateOnly airDate) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/calendar", request.RequestUri?.AbsolutePath);
            RequestCount++;
            var payload = new[]
            {
                new
                {
                    weekday = new { en = "Fri", cn = "星期五", ja = "金曜日", id = 5 },
                    items = new[]
                    {
                        new
                        {
                            id = animeId,
                            type = 2,
                            name = $"Calendar Anime {animeId}",
                            name_cn = $"日历动画{animeId}",
                            summary = "",
                            air_date = airDate.ToString("yyyy-MM-dd"),
                            air_weekday = 5,
                            images = (object?)null,
                            rating = (object?)null,
                        },
                    },
                },
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            });
        }
    }
}
