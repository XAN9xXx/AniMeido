using AniMeido.Plugin.Player.Diagnostics;
using AniMeido.Plugin.Player.Sources.Web;
using System.Net;

namespace AniMeido.Tests;

public sealed class HttpPageMediaResolverTests
{
    private const string MediaPattern = """(?<v>[^"'<> \r\n]+?\.(?:m3u8|mp4)(?:\?[^"'<> \r\n]*)?)""";
    private static readonly Uri Page = new("https://site.example.test/watch/index.html");
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task DirectMedia_UsesFinalResponseUriAndPreservesRequestContext()
    {
        var landing = new Uri("https://redirect.example.test/landing/index.html");
        using var handler = new ControlledHandler(_ => Response(
            "<video src='clip.m3u8'></video>", landing));
        await using var fixture = new Fixture(handler);

        var result = await fixture.Resolver.TryResolveAsync(Request(), CancellationToken.None)
            .WaitAsync(Budget);

        Assert.NotNull(result);
        Assert.Equal(new Uri(landing, "clip.m3u8"), result.Uri);
        Assert.Equal(landing.AbsoluteUri, result.Headers["Referer"]);
        Assert.Equal("https://redirect.example.test", result.Headers["Origin"]);
        Assert.Equal("fixture-agent", result.Headers["User-Agent"]);
        Assert.Equal("fixture=1", result.Headers["Cookie"]);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(Page, request.Uri);
        Assert.Equal("fixture-agent", request.UserAgent);
        Assert.Equal("fixture=1", request.Cookie);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NestedMedia_UsesNestedFinalUriAndKeepsExplicitHeaders(bool explicitHeaders)
    {
        var nested = new Uri(Page, "../player/embed.html");
        var landing = new Uri("https://nested.example.test/final/player.html");
        using var handler = new ControlledHandler(request => request.RequestUri == Page
            ? Response("<iframe src='../player/embed.html'></iframe>", Page)
            : Response("<source src='clip.mp4'>", landing));
        await using var fixture = new Fixture(handler);
        var request = Request(nested: true);
        if (explicitHeaders)
        {
            request = request with
            {
                Headers = new Dictionary<string, string>(request.Headers)
                {
                    ["Referer"] = "https://explicit.example.test/watch",
                    ["Origin"] = "https://explicit.example.test",
                },
            };
        }

        var result = await fixture.Resolver.TryResolveAsync(request, CancellationToken.None)
            .WaitAsync(Budget);

        Assert.NotNull(result);
        Assert.Equal(new Uri(landing, "clip.mp4"), result.Uri);
        Assert.Equal(explicitHeaders ? "https://explicit.example.test/watch" : landing.AbsoluteUri,
            result.Headers["Referer"]);
        Assert.Equal(explicitHeaders ? "https://explicit.example.test" : "https://nested.example.test",
            result.Headers["Origin"]);
        Assert.Equal("fixture-agent", result.Headers["User-Agent"]);
        Assert.Equal("fixture=1", result.Headers["Cookie"]);
        Assert.Equal(new[] { Page, nested }, handler.Requests.Select(item => item.Uri));
        Assert.All(handler.Requests, item =>
        {
            Assert.Equal("fixture-agent", item.UserAgent);
            Assert.Equal("fixture=1", item.Cookie);
            Assert.Equal(explicitHeaders ? "https://explicit.example.test/watch" : null, item.Referer);
            Assert.Equal(explicitHeaders ? "https://explicit.example.test" : null, item.Origin);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoMediaOrNestedLink_ReturnsNullWithoutAnotherRequest(bool nested)
    {
        using var handler = new ControlledHandler(_ => Response(
            "<html><div class='player'>loading</div></html>", Page));
        await using var fixture = new Fixture(handler);

        var result = await fixture.Resolver.TryResolveAsync(Request(nested), CancellationToken.None)
            .WaitAsync(Budget);

        Assert.Null(result);
        Assert.Equal(Page, Assert.Single(handler.Requests).Uri);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, (int)SourceResolutionFailureKind.Authentication)]
    [InlineData(HttpStatusCode.Forbidden, (int)SourceResolutionFailureKind.AccessDenied)]
    [InlineData(HttpStatusCode.NotFound, (int)SourceResolutionFailureKind.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests, (int)SourceResolutionFailureKind.RateLimited)]
    public async Task PageFailure_PreservesAccessClassification(HttpStatusCode status, int expectedKind)
    {
        using var handler = new ControlledHandler(_ => Response("rejected", Page, status));
        await using var fixture = new Fixture(handler);

        var error = await Assert.ThrowsAsync<SourceResolutionException>(() =>
            fixture.Resolver.TryResolveAsync(Request(), CancellationToken.None).WaitAsync(Budget));

        Assert.Equal((SourceResolutionFailureKind)expectedKind, error.Kind);
        Assert.Equal(Page, error.PageUri);
        Assert.Equal($"源站返回 HTTP {(int)status}。", error.Message);
        Assert.Equal(Page, Assert.Single(handler.Requests).Uri);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task NestedFailure_RemainsHttpRequestFailure(HttpStatusCode status)
    {
        var nested = new Uri(Page, "../player/embed.html");
        using var handler = new ControlledHandler(request => request.RequestUri == Page
            ? Response("<iframe src='../player/embed.html'></iframe>", Page)
            : Response("rejected", nested, status));
        await using var fixture = new Fixture(handler);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            fixture.Resolver.TryResolveAsync(Request(nested: true), CancellationToken.None).WaitAsync(Budget));

        Assert.Equal(status, error.StatusCode);
        Assert.Equal(new[] { Page, nested }, handler.Requests.Select(item => item.Uri));
    }

    private static WebResolutionRequest Request(bool nested = false) => new(
        "http-page-fixture", Page, MediaPattern, nested,
        nested ? "https?://nested\\.example\\.test/[^'\"<> ]+" : null,
        true, true, new Dictionary<string, string> { ["User-Agent"] = "fixture-agent" },
        "fixture=1", SourceDeclaredTimeout: TimeSpan.FromSeconds(10));

    private static HttpResponseMessage Response(
        string body, Uri effectiveUri, HttpStatusCode status = HttpStatusCode.OK) => new(status)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "text/html"),
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, effectiveUri),
        };

    private sealed record RecordedRequest(Uri Uri, string UserAgent, string? Cookie, string? Referer, string? Origin);

    private sealed class ControlledHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        internal List<RecordedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(new RecordedRequest(
                request.RequestUri!,
                request.Headers.UserAgent.ToString(),
                request.Headers.TryGetValues("Cookie", out var cookies) ? string.Join("; ", cookies) : null,
                request.Headers.Referrer?.AbsoluteUri,
                request.Headers.TryGetValues("Origin", out var origins) ? Assert.Single(origins) : null));
            return Task.FromResult(respond(request));
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"AniMeido-http-page-{Guid.NewGuid():N}");
        private readonly HttpClient _http;
        private readonly PlaybackDiagnosticRecorder _diagnostics;

        internal HttpPageMediaResolver Resolver { get; }

        internal Fixture(HttpMessageHandler handler)
        {
            _http = new HttpClient(handler, disposeHandler: false);
            _diagnostics = new PlaybackDiagnosticRecorder(_root);
            Resolver = new HttpPageMediaResolver(_http, _diagnostics);
        }

        public async ValueTask DisposeAsync()
        {
            _http.Dispose();
            await _diagnostics.DisposeAsync().AsTask().WaitAsync(Budget);
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
    }
}
