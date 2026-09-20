using AniMeido.Plugin.Base.Services;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text;

namespace AniMeido.Tests;

/// <summary>Archive 数据过旧时改为优先访问在线代理。</summary>
public sealed class BangumiArchiveFreshnessTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Monitor_PrefersOnlineApiWhenTheDumpIsOld()
    {
        var time = new TestTime(Now);
        var health = new HealthEndpoint(Now.AddDays(-20));
        var monitor = CreateMonitor(health, time);

        await monitor.EnsureCheckedAsync(CancellationToken.None);

        Assert.True(monitor.PreferFallback);
        Assert.Equal(1, health.Requests);
    }

    [Fact]
    public async Task Monitor_KeepsArchiveFirstWhenTheDumpIsRecent()
    {
        var monitor = CreateMonitor(new HealthEndpoint(Now.AddDays(-4)), new TestTime(Now));

        await monitor.EnsureCheckedAsync(CancellationToken.None);

        Assert.False(monitor.PreferFallback);
    }

    [Fact]
    public async Task Monitor_KeepsTheCurrentOrderWhenTheCheckFails()
    {
        // 健康检查失败不改变顺序：Archive 连不上由原有的降级处理。
        var time = new TestTime(Now);
        var health = new HealthEndpoint(createdAt: null) { Fails = true };
        var monitor = CreateMonitor(health, time);

        await monitor.EnsureCheckedAsync(CancellationToken.None);
        Assert.False(monitor.PreferFallback);
        Assert.Equal(1, health.Requests);

        // 失败后早一点重试，但不是每次请求都试。
        time.UtcNow = Now.AddMinutes(5);
        await monitor.EnsureCheckedAsync(CancellationToken.None);
        await monitor.PendingCheck;
        Assert.Equal(1, health.Requests);

        time.UtcNow = Now + ArchiveFreshnessMonitor.RetryInterval + TimeSpan.FromMinutes(1);
        health.Fails = false;
        health.CreatedAt = Now.AddDays(-20);
        await monitor.EnsureCheckedAsync(CancellationToken.None);
        await monitor.PendingCheck;

        Assert.Equal(2, health.Requests);
        Assert.True(monitor.PreferFallback);
    }

    [Fact]
    public async Task Monitor_ChecksAgainOnlyAfterTheInterval()
    {
        var time = new TestTime(Now);
        var health = new HealthEndpoint(Now.AddDays(-2));
        var monitor = CreateMonitor(health, time);

        await monitor.EnsureCheckedAsync(CancellationToken.None);
        time.UtcNow = Now + ArchiveFreshnessMonitor.CheckInterval - TimeSpan.FromMinutes(1);
        await monitor.EnsureCheckedAsync(CancellationToken.None);
        await monitor.PendingCheck;
        Assert.Equal(1, health.Requests);

        // 到期后在后台重新检查，这时数据已经过旧。
        time.UtcNow = Now + ArchiveFreshnessMonitor.CheckInterval + TimeSpan.FromMinutes(1);
        health.CreatedAt = time.UtcNow - ArchiveFreshnessMonitor.StaleAfter - TimeSpan.FromDays(1);
        await monitor.EnsureCheckedAsync(CancellationToken.None);
        await monitor.PendingCheck;

        Assert.Equal(2, health.Requests);
        Assert.True(monitor.PreferFallback);
    }

    [Fact]
    public async Task Client_AsksTheOnlineApiFirstWhenTheArchiveIsStale()
    {
        var factory = CreateFactory(
            (_, _) => Task.FromResult(JsonResponse("{\"value\":\"archive\"}")),
            (_, _) => Task.FromResult(JsonResponse("{\"value\":\"online\"}")));
        var client = new BangumiApiClient(
            factory,
            NullLogger<BangumiApiClient>.Instance,
            new StubFreshness(preferFallback: true));

        var result = await client.GetJsonAsync<TestPayload>("/v0/subjects/1", CancellationToken.None);

        Assert.Equal("online", result?.Value);
        Assert.Equal([BangumiApiClient.FallbackClientName], factory.CreatedClientNames);
    }

    [Fact]
    public async Task Client_StillFallsBackToTheArchiveWhenTheOnlineApiFails()
    {
        var factory = CreateFactory(
            (_, _) => Task.FromResult(JsonResponse("{\"value\":\"archive\"}")),
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        var client = new BangumiApiClient(
            factory,
            NullLogger<BangumiApiClient>.Instance,
            new StubFreshness(preferFallback: true));

        var result = await client.GetJsonAsync<TestPayload>("/v0/subjects/1", CancellationToken.None);

        Assert.Equal("archive", result?.Value);
        Assert.Equal(
            [BangumiApiClient.FallbackClientName, BangumiApiClient.ArchiveClientName],
            factory.CreatedClientNames);
    }

    private static ArchiveFreshnessMonitor CreateMonitor(HealthEndpoint health, TestTime time)
        => new(
            CreateFactory(health.RespondAsync, (_, _) => Task.FromResult(JsonResponse("{}"))),
            NullLogger.Instance,
            time);

    private static NamedHttpClientFactory CreateFactory(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> archive,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> fallback)
        => new(
            new HttpClient(new DelegateHandler(archive)) { BaseAddress = new Uri("https://archive.example.test") },
            new HttpClient(new DelegateHandler(fallback)) { BaseAddress = new Uri("https://fallback.example.test") });

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private sealed record TestPayload(string Value);

    private sealed class StubFreshness(bool preferFallback) : IArchiveFreshness
    {
        public bool PreferFallback { get; } = preferFallback;

        public Task EnsureCheckedAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class TestTime(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    /// <summary>假的 /healthz：按设定返回数据包生成时间，或直接失败。</summary>
    private sealed class HealthEndpoint(DateTimeOffset? createdAt)
    {
        public DateTimeOffset? CreatedAt { get; set; } = createdAt;

        public bool Fails { get; set; }

        public int Requests { get; private set; }

        public Task<HttpResponseMessage> RespondAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("/healthz", request.RequestUri?.AbsolutePath);
            Requests++;
            if (Fails)
                throw new HttpRequestException("offline");

            var created = CreatedAt?.ToString("O") ?? "";
            return Task.FromResult(JsonResponse(
                $"{{\"status\":\"ok\",\"archive\":{{\"archive_created_at\":\"{created}\"}}}}"));
        }
    }

    private sealed class NamedHttpClientFactory(
        HttpClient archiveClient,
        HttpClient fallbackClient) : IHttpClientFactory
    {
        public List<string> CreatedClientNames { get; } = [];

        public HttpClient CreateClient(string name)
        {
            CreatedClientNames.Add(name);
            return name switch
            {
                BangumiApiClient.ArchiveClientName => archiveClient,
                BangumiApiClient.FallbackClientName => fallbackClient,
                _ => throw new InvalidOperationException($"Unknown client: {name}"),
            };
        }
    }

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => responder(request, cancellationToken);
    }
}
