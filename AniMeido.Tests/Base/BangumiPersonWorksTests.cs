using AniMeido.Plugin.Base.Services;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text;

namespace AniMeido.Tests;

/// <summary>人物作品：本地 Archive 带评分与日期，在线接口没有这两项。</summary>
public sealed class BangumiPersonWorksTests : DbTestBase
{
    [Fact]
    public async Task GetPersonWorksAsync_ReadsScoreAndDateFromTheArchive()
    {
        await CreateBaseTablesAsync();
        var dataSource = CreateDataSource("""
            [
              {"id":1,"type":2,"name":"作品1","name_cn":"作品一","staff":"动画制作","eps":"12",
               "image":"https://lain.example.test/pic/1.jpg",
               "date":"2018-01-07","rating":{"score":8.4,"rank":120,"total":900}},
              {"id":2,"type":2,"name":"作品2","name_cn":null,"staff":"动画制作","eps":null,
               "image":null,"date":null,"rating":{"score":0,"rank":0,"total":0}},
              {"id":3,"type":1,"name":"漫画","name_cn":null,"staff":"原作","eps":null,"image":null}
            ]
            """);

        var works = await dataSource.GetPersonWorksAsync(901, CancellationToken.None);

        // 只保留动画。
        Assert.Equal(new[] { 1, 2 }, works.Select(work => work.ID));
        var first = works[0];
        Assert.Equal("作品一", first.Title);
        Assert.Equal(8.4, first.Score);
        Assert.Equal(new DateOnly(2018, 1, 7), first.AirDate);
        // 评分为 0 表示上游没有评分，不当作 0 分。
        Assert.Null(works[1].Score);
        Assert.Null(works[1].AirDate);
    }

    [Fact]
    public async Task GetPersonWorksAsync_LeavesScoreEmptyWhenTheOnlineApiAnswers()
    {
        // 在线接口的人物作品没有 rating 与 date 字段。
        await CreateBaseTablesAsync();
        var dataSource = CreateDataSource("""
            [{"id":5,"type":2,"name":"作品5","name_cn":null,"staff":"动画制作","eps":"24","image":null}]
            """);

        var works = await dataSource.GetPersonWorksAsync(902, CancellationToken.None);

        var work = Assert.Single(works);
        Assert.Null(work.Score);
        Assert.Null(work.AirDate);
        Assert.Equal("动画制作", work.Staff);
    }

    private BangumiDataSource CreateDataSource(string json)
    {
        var handler = new JsonHandler(json);
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://archive.example.test") };
        var apiClient = new BangumiApiClient(
            new StubHttpClientFactory(client),
            NullLogger<BangumiApiClient>.Instance,
            new FreshArchive());
        return new BangumiDataSource(
            NullLogger<BangumiDataSource>.Instance,
            apiClient,
            new CacheService(DbFactory));
    }

    private sealed class FreshArchive : IArchiveFreshness
    {
        public bool PreferFallback => false;

        public Task EnsureCheckedAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class JsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.StartsWith("/v0/persons/", request.RequestUri?.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }
}
