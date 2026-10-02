using AniMeido.Plugin.Player.Diagnostics;
using AniMeido.Plugin.Player.Sources.Web;
using System.Diagnostics;
using System.Net;

namespace AniMeido.Tests;

public sealed class WebMediaResolverTimeoutTests
{
    private static readonly Uri Page = new("https://timeout.example.test/page");
    private static readonly Uri Media = new("https://timeout.example.test/video.m3u8");
    private static readonly TimeSpan ShortBudget = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Resolve_PageAndFirstProbeShareTenSecondBudget()
    {
        var probeEntered = false;
        using var handler = new ControlledHandler(async (request, token) =>
        {
            if (request.RequestUri == Page)
            {
                await Task.Delay(TimeSpan.FromSeconds(6), token);
                return PageResponse();
            }
            probeEntered = true;
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Unreachable");
        });
        await using var fixture = new Fixture(handler, TimeSpan.FromSeconds(60));
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(14));
        var stopwatch = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<SourceResolutionException>(() =>
            fixture.Resolver.ResolveAsync(Request(), guard.Token));
        AssertTimeout(error);
        Assert.True(probeEntered);
        Assert.False(guard.IsCancellationRequested);
        Assert.InRange(stopwatch.Elapsed.TotalSeconds, 9, 14);
    }

    [Fact]
    public async Task Resolve_HttpTimeoutDuringProbeIsTimeoutNotCancellation()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = ProbeHandler(entered);
        await using var fixture = new Fixture(handler, TimeSpan.FromMilliseconds(100));
        var pending = fixture.Resolver.ResolveAsync(Request(), CancellationToken.None);
        await entered.Task.WaitAsync(ShortBudget);
        var error = await Assert.ThrowsAsync<SourceResolutionException>(() => pending.WaitAsync(ShortBudget));
        AssertTimeout(error);
    }

    [Fact]
    public async Task Resolve_CallerCancellationDuringProbeRemainsOperationCanceled()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = ProbeHandler(entered);
        await using var fixture = new Fixture(handler, TimeSpan.FromSeconds(60));
        using var cancellation = new CancellationTokenSource();
        var pending = fixture.Resolver.ResolveAsync(Request(), cancellation.Token);
        await entered.Task.WaitAsync(ShortBudget);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(ShortBudget));
    }

    private static void AssertTimeout(SourceResolutionException error)
    {
        Assert.Equal(SourceResolutionFailureKind.Timeout, error.Kind);
        Assert.Equal("播放源解析超过 10 秒。", error.Message);
        Assert.Equal(Page, error.PageUri);
        Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
    }

    private static ControlledHandler ProbeHandler(TaskCompletionSource entered) => new(async (request, token) =>
    {
        if (request.RequestUri == Page) return PageResponse();
        Assert.Equal(Media, request.RequestUri);
        entered.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        throw new InvalidOperationException("Unreachable");
    });

    private static HttpResponseMessage PageResponse() => new(HttpStatusCode.OK)
    {
        Content = new StringContent($"<video src='{Media}'></video>", System.Text.Encoding.UTF8, "text/html"),
        RequestMessage = new HttpRequestMessage(HttpMethod.Get, Page),
    };
    private static WebResolutionRequest Request() => new("timeout-fixture", Page,
        """(?<v>https?://[^'"<> ]+\.m3u8)""", false, null, true, true,
        new Dictionary<string, string>(), "fixture=1", SourceDeclaredTimeout: TimeSpan.FromSeconds(10));

    private sealed class ControlledHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => respond(request, cancellationToken);
    }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"AniMeido-web-timeout-{Guid.NewGuid():N}");
        private readonly HttpClient _http;
        private readonly PlaybackDiagnosticRecorder _diagnostics;
        public WebMediaResolver Resolver { get; }
        public Fixture(HttpMessageHandler handler, TimeSpan timeout)
        {
            _http = new HttpClient(handler, disposeHandler: false) { Timeout = timeout };
            _diagnostics = new PlaybackDiagnosticRecorder(_root);
            Resolver = new WebMediaResolver(_http, new PlayerRuntimeSettingsStore(Path.Combine(_root, "runtime.json")),
                new HostWebSessionManager(_root), _diagnostics);
        }
        public async ValueTask DisposeAsync()
        {
            Resolver.Dispose();
            _http.Dispose();
            await _diagnostics.DisposeAsync();
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }
}
