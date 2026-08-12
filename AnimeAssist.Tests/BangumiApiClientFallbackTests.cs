using AniMeido.Plugin.Base.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text;

namespace AniMeido.Tests;

public sealed class BangumiApiClientFallbackTests
{
    [Fact]
    public void AddBangumiService_RegistersArchiveBeforeWorkerFallback()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBangumiService();
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        var archive = factory.CreateClient(BangumiApiClient.ArchiveClientName);
        var fallback = factory.CreateClient(BangumiApiClient.FallbackClientName);

        Assert.Equal("https://archive.animeido.com:38443/", archive.BaseAddress?.AbsoluteUri);
        Assert.Equal("https://bgm-proxy.animeido.com/", fallback.BaseAddress?.AbsoluteUri);
        Assert.True(archive.Timeout < fallback.Timeout);
    }

    [Fact]
    public async Task GetJsonAsync_UsesArchiveWithoutCallingFallback()
    {
        var archiveRequests = 0;
        var fallbackRequests = 0;
        var factory = CreateFactory(
            async (_, cancellationToken) =>
            {
                archiveRequests++;
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                return JsonResponse("{\"value\":\"archive\"}");
            },
            (_, _) =>
            {
                fallbackRequests++;
                return Task.FromResult(JsonResponse("{\"value\":\"fallback\"}"));
            });
        var client = CreateClient(factory);

        var result = await client.GetJsonAsync<TestPayload>("/healthz", CancellationToken.None);

        Assert.Equal("archive", result?.Value);
        Assert.Equal(1, archiveRequests);
        Assert.Equal(0, fallbackRequests);
    }

    [Fact]
    public async Task GetJsonAsync_FallsBackWhenArchiveReturnsInvalidJson()
    {
        var factory = CreateFactory(
            (_, _) => Task.FromResult(JsonResponse("not-json")),
            (_, _) => Task.FromResult(JsonResponse("{\"value\":\"fallback\"}")));
        var client = CreateClient(factory);

        var result = await client.GetJsonAsync<TestPayload>("/v0/subjects/1", CancellationToken.None);

        Assert.Equal("fallback", result?.Value);
        Assert.Equal(
            [BangumiApiClient.ArchiveClientName, BangumiApiClient.FallbackClientName],
            factory.CreatedClientNames);
    }

    [Fact]
    public async Task PostJsonAsync_FallsBackAndRecreatesRequestBody()
    {
        string? archiveBody = null;
        string? fallbackBody = null;
        var factory = CreateFactory(
            async (request, cancellationToken) =>
            {
                archiveBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            },
            async (request, cancellationToken) =>
            {
                fallbackBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                return JsonResponse("{\"value\":\"fallback\"}");
            });
        var client = CreateClient(factory);

        var result = await client.PostJsonAsync<TestPayload>(
            "/v0/search/subjects",
            new { keyword = "测试" },
            CancellationToken.None);

        Assert.Equal("fallback", result?.Value);
        Assert.Equal(archiveBody, fallbackBody);
        Assert.NotNull(fallbackBody);
    }

    [Fact]
    public async Task GetJsonAsync_DoesNotFallbackAfterCallerCancellation()
    {
        var fallbackRequests = 0;
        var factory = CreateFactory(
            async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return JsonResponse("{}");
            },
            (_, _) =>
            {
                fallbackRequests++;
                return Task.FromResult(JsonResponse("{}"));
            });
        var client = CreateClient(factory);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetJsonAsync<TestPayload>("/calendar", cancellation.Token));

        Assert.Equal(0, fallbackRequests);
        Assert.Equal([BangumiApiClient.ArchiveClientName], factory.CreatedClientNames);
    }

    private static BangumiApiClient CreateClient(IHttpClientFactory factory)
    {
        return new BangumiApiClient(factory, NullLogger<BangumiApiClient>.Instance);
    }

    private static NamedHttpClientFactory CreateFactory(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> archive,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> fallback)
    {
        return new NamedHttpClientFactory(
            CreateHttpClient(archive, "https://archive.example.test"),
            CreateHttpClient(fallback, "https://fallback.example.test"));
    }

    private static HttpClient CreateHttpClient(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder,
        string baseAddress)
    {
        return new HttpClient(new DelegateHandler(responder))
        {
            BaseAddress = new Uri(baseAddress),
        };
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    private sealed record TestPayload(string Value);

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
        {
            return responder(request, cancellationToken);
        }
    }
}
