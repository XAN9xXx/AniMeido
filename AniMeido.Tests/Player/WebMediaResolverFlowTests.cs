using AniMeido.Plugin.Player.Diagnostics;
using AniMeido.Plugin.Player.Sources.Web;
using System.Net;

namespace AniMeido.Tests;

public sealed class WebMediaResolverFlowTests
{
    private const string MediaPattern = """(?<v>[^"'<> \r\n]+?\.(?:m3u8|mp4)(?:\?[^"'<> \r\n]*)?)""";
    private static readonly Uri Page = new("https://site.example.test/watch/index.html");
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Resolve_RedirectedResponseUsesFinalPageForRelativeMediaAndReferer()
    {
        var landing = new Uri("https://redirect.example.test/landing/index.html");
        var media = new Uri(landing, "clip.m3u8");
        using var handler = new ControlledHandler((request, _) => Task.FromResult(
            request.RequestUri == Page
                ? Response("<video src=\"clip.m3u8\"></video>", "text/html", landing)
                : Response("#EXTM3U\n#EXT-X-VERSION:3", "application/vnd.apple.mpegurl", request.RequestUri!)));
        await using var fixture = new ResolverFixture(handler);
        var result = await fixture.Resolver.ResolveAsync(Request(), CancellationToken.None).WaitAsync(Budget);
        Assert.Equal(media, result.Uri);
        Assert.Equal(landing.AbsoluteUri, result.Headers["Referer"]);
        Assert.Equal("https://redirect.example.test", result.Headers["Origin"]);
        Assert.Equal(new[] { Page, media }, handler.Requests.Select(item => item.Uri));
    }

    [Fact]
    public async Task Resolve_IframeAndEscapedJsonMediaPreserveNestedRefererAndHeaders()
    {
        var nested = new Uri(Page, "../player/embed.html");
        var media = new Uri("https://cdn.example.test/stream.m3u8?key=fixture");
        using var handler = new ControlledHandler((request, _) => Task.FromResult(
            request.RequestUri == Page ? Response("<iframe src='../player/embed.html'></iframe>", "text/html", Page)
            : request.RequestUri == nested ? Response("""{"video":"https:\/\/cdn.example.test\/stream.m3u8?key=fixture"}""", "application/json", nested)
            : Response("#EXTM3U\n#EXTINF:4,\nsegment.ts", "text/plain", media)));
        await using var fixture = new ResolverFixture(handler);
        var result = await fixture.Resolver.ResolveAsync(Request(nested: true), CancellationToken.None).WaitAsync(Budget);
        Assert.Equal(media, result.Uri);
        Assert.Equal(nested.AbsoluteUri, result.Headers["Referer"]);
        Assert.Equal("fixture-agent", result.Headers["User-Agent"]);
        Assert.Equal("fixture=1", result.Headers["Cookie"]);
        Assert.Equal(new[] { Page, nested, media }, handler.Requests.Select(item => item.Uri));
        Assert.Equal(nested.AbsoluteUri, handler.Requests[2].Referer);
        Assert.Null(handler.Requests[2].Range);
    }

    [Theory]
    [InlineData("m3u8")]
    [InlineData("mp4")]
    public async Task Resolve_FakeMediaReturningHtmlIsRejectedDespiteExtension(string extension)
    {
        var media = new Uri($"https://cdn.example.test/fake.{extension}");
        using var handler = new ControlledHandler((request, _) => Task.FromResult(
            request.RequestUri == Page ? Response($"<video src='{media}'></video>", "text/html", Page)
            : Response("<html><body>not video</body></html>", "text/html", media)));
        await using var fixture = new ResolverFixture(handler);
        var error = await Assert.ThrowsAsync<SourceResolutionException>(() =>
            fixture.Resolver.ResolveAsync(Request(), CancellationToken.None).WaitAsync(Budget));
        Assert.Equal(SourceResolutionFailureKind.MediaRejected, error.Kind);
        Assert.Equal(Page, error.PageUri);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(extension == "mp4" ? "bytes=0-1023" : null, handler.Requests[1].Range);
    }

    [Fact]
    public async Task Resolve_VideoContentTypeUsesRangeProbe()
    {
        var media = new Uri("https://cdn.example.test/real.mp4");
        using var handler = new ControlledHandler((request, _) => Task.FromResult(
            request.RequestUri == Page ? Response($"<source src='{media}'>", "text/html", Page)
            : Response("fixture-video-bytes", "video/mp4", media)));
        await using var fixture = new ResolverFixture(handler);
        var result = await fixture.Resolver.ResolveAsync(Request(), CancellationToken.None).WaitAsync(Budget);
        Assert.Equal(media, result.Uri);
        Assert.Equal("bytes=0-1023", handler.Requests[1].Range);
    }

    [Fact]
    public async Task Resolve_Mp4SignatureCanBeSniffedWhenContentTypeIsWrong()
    {
        var media = new Uri("https://cdn.example.test/sniffed.mp4");
        // ISO BMFF ftyp 头：探测容器签名，不声称验证解码器或完整视频流。
        byte[] prefix = [0, 0, 0, 24, 102, 116, 121, 112, 105, 115, 111, 109,
            0, 0, 0, 0, 105, 115, 111, 109, 109, 112, 52, 50];
        using var handler = new ControlledHandler((request, _) =>
        {
            if (request.RequestUri == Page)
                return Task.FromResult(Response($"<video src='{media}'>", "text/html", Page));
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(prefix) };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
            return Task.FromResult(response);
        });
        await using var fixture = new ResolverFixture(handler);
        var result = await fixture.Resolver.ResolveAsync(Request(), CancellationToken.None).WaitAsync(Budget);
        Assert.Equal(media, result.Uri);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("bytes=0-1023", handler.Requests[1].Range);
    }
    [Fact]
    public async Task Resolve_CancellationDuringHttpPropagatesAndNextRequestStillWorks()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        using var handler = new ControlledHandler(async (request, token) =>
        {
            if (++attempts == 1) { entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            return request.RequestUri == Page
                ? Response("<video src='https://cdn.example.test/ok.m3u8'>", "text/html", Page)
                : Response("#EXTM3U", "text/plain", request.RequestUri!);
        });
        await using var fixture = new ResolverFixture(handler);
        using var cancellation = new CancellationTokenSource();
        var pending = fixture.Resolver.ResolveAsync(Request(), cancellation.Token);
        await entered.Task.WaitAsync(Budget);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Budget));
        var result = await fixture.Resolver.ResolveAsync(Request(), CancellationToken.None).WaitAsync(Budget);
        Assert.Equal("/ok.m3u8", result.Uri.AbsolutePath);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Resolve_HttpTimeoutBecomesTimeoutFailureNotCallerCancellation()
    {
        var cancelled = false;
        using var handler = new ControlledHandler(async (_, token) =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) { cancelled = true; throw; }
            throw new InvalidOperationException("Unreachable");
        });
        await using var fixture = new ResolverFixture(handler, TimeSpan.FromMilliseconds(100));
        var error = await Assert.ThrowsAsync<SourceResolutionException>(() =>
            fixture.Resolver.ResolveAsync(Request(), CancellationToken.None).WaitAsync(Budget));
        Assert.Equal(SourceResolutionFailureKind.Timeout, error.Kind);
        Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
        Assert.True(cancelled);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Resolve_ChangedHtmlWithoutMediaRequiresBrowserFallbackButDoesNotOpenUiInTest()
    {
        using var handler = new ControlledHandler((_, _) => Task.FromResult(
            Response("<html><div class='new-player'>loading</div></html>", "text/html", Page)));
        await using var fixture = new ResolverFixture(handler);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Resolver.ResolveAsync(Request(), CancellationToken.None).WaitAsync(Budget));
        Assert.Equal("网页解析器尚未绑定播放器 UI 线程。", error.Message);
        Assert.Single(handler.Requests);
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "WebView2")));
    }

    private static WebResolutionRequest Request(bool nested = false) => new(
        "offline-fixture", Page, MediaPattern, nested, nested ? ".*" : null,
        true, true, new Dictionary<string, string> { ["User-Agent"] = "fixture-agent" },
        "fixture=1", SourceDeclaredTimeout: TimeSpan.FromSeconds(10));

    private static HttpResponseMessage Response(string body, string type, Uri effectiveUri) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, type),
        RequestMessage = new HttpRequestMessage(HttpMethod.Get, effectiveUri),
    };

    private sealed class ControlledHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<(Uri Uri, string? Referer, string? Range)> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.RequestUri!, request.Headers.Referrer?.AbsoluteUri, request.Headers.Range?.ToString()));
            return respond(request, ct);
        }
    }

    private sealed class ResolverFixture : IAsyncDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"AniMeido-web-flow-{Guid.NewGuid():N}");
        private readonly HttpClient _http;
        private readonly PlaybackDiagnosticRecorder _diagnostics;
        public WebMediaResolver Resolver { get; }
        public ResolverFixture(HttpMessageHandler handler, TimeSpan? httpTimeout = null)
        {
            _http = new HttpClient(handler, disposeHandler: false);
            if (httpTimeout is { } timeout) _http.Timeout = timeout;
            _diagnostics = new PlaybackDiagnosticRecorder(Root);
            Resolver = new WebMediaResolver(_http,
                new PlayerRuntimeSettingsStore(Path.Combine(Root, "runtime.json")),
                new HostWebSessionManager(Root), _diagnostics);
        }
        public async ValueTask DisposeAsync()
        {
            Resolver.Dispose();
            _http.Dispose();
            await _diagnostics.DisposeAsync();
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
