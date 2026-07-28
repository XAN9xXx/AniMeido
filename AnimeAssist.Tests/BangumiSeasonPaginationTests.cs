using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text;
using System.Text.Json;

namespace AniMeido.Tests;

public sealed class BangumiSeasonPaginationTests : DbTestBase
{
    [Fact]
    public async Task GetAnimeBySeasonAsync_LoadsAllPagesAndIgnoresLegacyCache()
    {
        await CreateBaseTablesAsync();
        await SeedLegacyTruncatedCacheAsync();

        var handler = new SeasonApiHandler(total: 45);
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.example.test"),
        };
        var apiClient = new BangumiApiClient(
            new StubHttpClientFactory(client),
            NullLogger<BangumiApiClient>.Instance);
        var dataSource = new BangumiDataSource(
            NullLogger<BangumiDataSource>.Instance,
            apiClient,
            new CacheService(DbFactory));

        var result = await dataSource.GetAnimeBySeasonAsync(
            2025,
            Season.Spring,
            CancellationToken.None);

        Assert.Equal(45, result.Count);
        Assert.Equal(
            new[] { 0, 20, 40 },
            handler.RequestedOffsets);
        Assert.Equal(45, result.Select(anime => anime.ID).Distinct().Count());
        Assert.Equal(
            45,
            await ReadCachedCountAsync("season:v2:2025:Spring"));
    }

    [Fact]
    public async Task GetAnimeBySeasonAsync_InvalidCurrentCache_IsReplaced()
    {
        await CreateBaseTablesAsync();
        var cache = new CacheService(DbFactory);
        await cache.SetCacheAsync(
            "season:v2:2025:Spring",
            "{invalid-json",
            TimeSpan.FromHours(1));
        var handler = new SeasonApiHandler(total: 3);
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.example.test"),
        };
        var dataSource = new BangumiDataSource(
            NullLogger<BangumiDataSource>.Instance,
            new BangumiApiClient(
                new StubHttpClientFactory(client),
                NullLogger<BangumiApiClient>.Instance),
            cache);

        var result = await dataSource.GetAnimeBySeasonAsync(
            2025,
            Season.Spring,
            CancellationToken.None);

        Assert.Equal(3, result.Count);
        Assert.Equal([0], handler.RequestedOffsets);
        Assert.Equal(
            3,
            await ReadCachedCountAsync("season:v2:2025:Spring"));
    }

    private async Task SeedLegacyTruncatedCacheAsync()
    {
        using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO cache (CacheKey, Data, ExpiresAt)
            VALUES (@key, @data, @expiresAt)
            """;
        command.Parameters.AddWithValue("@key", "season:2025:Spring");
        command.Parameters.AddWithValue(
            "@data",
            JsonSerializer.Serialize(Enumerable.Range(1, 40)));
        command.Parameters.AddWithValue(
            "@expiresAt",
            DateTime.UtcNow.AddHours(1).ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private async Task<int> ReadCachedCountAsync(string key)
    {
        using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Data FROM cache WHERE CacheKey = @key";
        command.Parameters.AddWithValue("@key", key);
        var data = (string?)await command.ExecuteScalarAsync();
        Assert.NotNull(data);
        using var document = JsonDocument.Parse(data);
        return document.RootElement.GetArrayLength();
    }

    private sealed class StubHttpClientFactory(HttpClient client)
        : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class SeasonApiHandler(int total) : HttpMessageHandler
    {
        public List<int> RequestedOffsets { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get &&
                request.RequestUri?.AbsolutePath == "/calendar")
            {
                return JsonResponse("[]");
            }

            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(
                "/v0/search/subjects",
                request.RequestUri?.AbsolutePath);

            var offset = GetQueryValue(request.RequestUri, "offset");
            var limit = GetQueryValue(request.RequestUri, "limit");
            RequestedOffsets.Add(offset);

            var count = Math.Min(limit, total - offset);
            var data = Enumerable.Range(offset + 1, count)
                .Select(id => new
                {
                    id,
                    name = $"Anime {id}",
                    name_cn = (string?)null,
                    summary = (string?)null,
                    date = "2025-04-01",
                    images = (object?)null,
                    meta_tags = Array.Empty<string>(),
                });
            var json = JsonSerializer.Serialize(new
            {
                total,
                limit,
                offset,
                data,
            });

            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            return JsonResponse(json);
        }

        private static int GetQueryValue(Uri? uri, string name)
        {
            Assert.NotNull(uri);
            var pair = uri.Query.TrimStart('?')
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Split('=', 2))
                .Single(value => value[0] == name);
            return int.Parse(
                pair[1],
                System.Globalization.CultureInfo.InvariantCulture);
        }

        private static HttpResponseMessage JsonResponse(string json)
            => new(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    json,
                    Encoding.UTF8,
                    "application/json"),
            };
    }
}
