using System.Net;
using System.Net.Http.Headers;
using AniMeido.App.Services;
using AniMeido.Updates;

namespace AniMeido.Tests;

public sealed class UpdateTestSourceHandlerTests
{
    [Fact]
    public async Task ConditionalMappingSupportsLocalBytesRangeAndPassThrough()
    {
        var type = typeof(InstalledUpdateService).Assembly.GetType("AniMeido.App.Services.UpdateTestSourceHandler");
        if (type is null)
        {
            Assert.Empty(UpdateTrustedKeys.PublicKeys);
            return; // 普通构建只验证类型缺席；开启属性的测试构建才运行映射行为。
        }
        Assert.Single(UpdateTrustedKeys.PublicKeys);
        using var paths = new MockAppDataPaths();
        var previous = Environment.GetEnvironmentVariable("ANIMEIDO_UPDATE_TEST_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("ANIMEIDO_UPDATE_TEST_ROOT", paths.RootDirectory);
            var directory = Path.Combine(paths.RootDirectory, "win-x64", "stable");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "test.zip"), "0123456789");
            using var handler = (DelegatingHandler)Activator.CreateInstance(type, nonPublic: true)!;
            handler.InnerHandler = new OfflineHandler();
            using var client = new HttpClient(handler);
            using var full = await client.GetAsync("https://dl.animeido.com/updates/win-x64/stable/test.zip");
            Assert.Equal("0123456789", await full.Content.ReadAsStringAsync());
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "https://dl.animeido.com/updates/win-x64/stable/test.zip");
            request.Headers.Range = new RangeHeaderValue(4, null);
            using var range = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.PartialContent, range.StatusCode);
            Assert.Equal(4, range.Content.Headers.ContentRange!.From);
            Assert.Equal(10, range.Content.Headers.ContentRange.Length);
            Assert.Equal("456789", await range.Content.ReadAsStringAsync());
            using var missing = await client.GetAsync("https://dl.animeido.com/updates/win-x64/stable/missing.zip");
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            using var other = await client.GetAsync("https://example.invalid/unmapped");
            Assert.Equal("offline passthrough", await other.Content.ReadAsStringAsync());
            await Assert.ThrowsAsync<HttpRequestException>(() =>
                client.GetAsync("https://dl.animeido.com/updates/win-x64/stable/%5Coutside"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANIMEIDO_UPDATE_TEST_ROOT", previous);
        }
    }

    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { RequestMessage = request, Content = new StringContent("offline passthrough") });
    }
}
