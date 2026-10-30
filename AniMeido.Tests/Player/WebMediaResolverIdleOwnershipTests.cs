using AniMeido.Plugin.Player.Diagnostics;
using AniMeido.Plugin.Player.Sources.Web;
using System.Reflection;

namespace AniMeido.Tests;

[Trait("Category", "Mechanism")]
public sealed class WebMediaResolverIdleOwnershipTests
{
    private static readonly Uri Page = new("https://idle.example.test/watch");
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);
    private static readonly FieldInfo IdleCancellation = typeof(WebMediaResolver)
        .GetField("_idleCleanupCancellation", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Theory]
    [InlineData("resolve")]
    [InlineData("load-page")]
    public async Task BrowserOperationCancelsPreviousIdleCleanupAndSchedulesANewOne(string operation)
    {
        await using var fixture = new Fixture();
        var previousToken = fixture.PreviousIdle.Token;

        // 不绑定 UI，让真实入口在调度边界结束；不创建 WebView 或等待五分钟。
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RunOperationAsync(fixture.Resolver, operation).WaitAsync(Budget));

        Assert.Equal("网页解析器尚未绑定播放器 UI 线程。", error.Message);
        Assert.True(previousToken.IsCancellationRequested);
        var nextIdle = Assert.IsType<CancellationTokenSource>(
            IdleCancellation.GetValue(fixture.Resolver));
        Assert.NotSame(fixture.PreviousIdle, nextIdle);
        Assert.False(nextIdle.IsCancellationRequested);
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "WebView2")));
    }

    [Fact]
    public async Task TemporarySessionHostCancelsPreviousIdleCleanupAndRearmsTheRestoredPlayer()
    {
        await using var fixture = new Fixture();
        var player = new WebViewHostBinding((nint)1, (action, _) => action());
        var temporary = new WebViewHostBinding((nint)2, (action, _) => action());
        Assert.True(await fixture.Resolver.AttachUiThreadAsync(player).WaitAsync(Budget));
        fixture.ResetPreviousIdle();
        var previousToken = fixture.PreviousIdle.Token;
        var failure = new InvalidOperationException("Synthetic session operation failure.");

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Resolver.RunWithTemporaryHostAsync(temporary,
                _ => Task.FromException<bool>(failure), CancellationToken.None).WaitAsync(Budget));

        Assert.Same(failure, actual);
        Assert.True(previousToken.IsCancellationRequested);
        var nextIdle = Assert.IsType<CancellationTokenSource>(IdleCancellation.GetValue(fixture.Resolver));
        Assert.NotSame(fixture.PreviousIdle, nextIdle);
        Assert.False(nextIdle.IsCancellationRequested);
        Assert.Same(player, fixture.Resolver.CurrentBinding);
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "WebView2")));
    }

    private static Task RunOperationAsync(WebMediaResolver resolver, string operation)
        => operation switch
        {
            "resolve" => resolver.ResolveAsync(new WebResolutionRequest(
                "idle-fixture", Page, ".*", false, null, false, false,
                new Dictionary<string, string>(), Cookies: null), CancellationToken.None),
            "load-page" => resolver.LoadPageHtmlAsync(Page, interactive: false, CancellationToken.None),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly HttpClient _http = new(new RejectNetworkHandler());
        private readonly PlaybackDiagnosticRecorder _diagnostics;
        public string Root { get; } = Path.Combine(
            Path.GetTempPath(), $"AniMeido-web-idle-{Guid.NewGuid():N}");
        public CancellationTokenSource PreviousIdle { get; private set; } = new();
        public WebMediaResolver Resolver { get; }

        public Fixture()
        {
            _diagnostics = new PlaybackDiagnosticRecorder(Root);
            Resolver = new WebMediaResolver(
                _http,
                new PlayerRuntimeSettingsStore(Path.Combine(Root, "runtime.json")),
                new HostWebSessionManager(Root),
                _diagnostics);
            IdleCancellation.SetValue(Resolver, PreviousIdle);
        }

        public void ResetPreviousIdle()
        {
            // PreviousIdle 是夹具注入的裸 CTS，不由真实定时任务释放。
            PreviousIdle.Dispose();
            PreviousIdle = new CancellationTokenSource();
            IdleCancellation.SetValue(Resolver, PreviousIdle);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Resolver.DisposeAsync().AsTask().WaitAsync(Budget);
            }
            finally
            {
                PreviousIdle.Dispose();
                _http.Dispose();
                await _diagnostics.DisposeAsync();
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
        }
    }

    private sealed class RejectNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("该测试不应发送 HTTP 请求。");
    }
}
