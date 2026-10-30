using AniMeido.Plugin.Player.Diagnostics;
using AniMeido.Plugin.Player.Sources.Web;
using System.Reflection;

namespace AniMeido.Tests;

[Trait("Category", "Mechanism")]
public sealed class WebMediaResolverIdleDrainTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);
    private static readonly Uri Page = new("https://idle-drain.example.test/watch");
    private static readonly FieldInfo IdleTask = typeof(WebMediaResolver)
        .GetField("_idleCleanupTask", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Fact]
    public async Task DisposeWaitsForCancelledIdleCleanupFromEveryCallerContext()
    {
        await using var fixture = new Fixture();
        var olderContext = new ContinuationContext();
        var latestContext = new ContinuationContext();
        var disposingContext = new ContinuationContext();
        var operations = new List<Task>();
        var idleTasks = new List<Task>();
        Task? disposing = null;
        try
        {
            var firstRead = BeginUnboundRead(fixture.Resolver, olderContext);
            operations.Add(firstRead);
            await AssertMissingBindingAsync(firstRead);
            var olderCleanup = GetIdleTask(fixture.Resolver);
            idleTasks.Add(olderCleanup);

            var secondRead = BeginUnboundRead(fixture.Resolver, latestContext);
            operations.Add(secondRead);
            await AssertMissingBindingAsync(secondRead);
            idleTasks.Add(GetIdleTask(fixture.Resolver));
            await olderContext.FirstPost.WaitAsync(Budget);

            disposing = disposingContext.Run(() => fixture.Resolver.DisposeAsync().AsTask());
            await latestContext.FirstPost.WaitAsync(Budget);

            // 新计时已经退出，但旧计时的真实取消续体仍由第一个调用上下文扣住。
            latestContext.RunPending();
            disposingContext.RunPending();

            Assert.False(olderCleanup.IsCompleted);
            Assert.False(disposing.IsCompleted);
        }
        finally
        {
            disposing ??= disposingContext.Run(() => fixture.Resolver.DisposeAsync().AsTask());
            olderContext.Resume();
            latestContext.Resume();
            disposingContext.Resume();
            await Task.WhenAll(operations.Select(ObserveOperationAsync)).WaitAsync(Budget);
            await Task.WhenAll(idleTasks).WaitAsync(Budget);
            await disposing.WaitAsync(Budget);
        }
    }

    private static Task<string> BeginUnboundRead(WebMediaResolver resolver, ContinuationContext context)
        => context.Run(() => resolver.LoadPageHtmlAsync(Page, interactive: false, CancellationToken.None));

    private static Task GetIdleTask(WebMediaResolver resolver)
        => Assert.IsAssignableFrom<Task>(IdleTask.GetValue(resolver));

    private static async Task AssertMissingBindingAsync(Task operation)
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => operation.WaitAsync(Budget));
        Assert.Equal("网页解析器尚未绑定播放器 UI 线程。", error.Message);
    }

    private static async Task ObserveOperationAsync(Task operation)
        => await operation.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    private sealed class ContinuationContext : SynchronizationContext
    {
        private readonly object _sync = new();
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _pending = [];
        private readonly TaskCompletionSource _firstPost = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _resumed;

        public Task FirstPost => _firstPost.Task;

        public override void Post(SendOrPostCallback callback, object? state)
        {
            lock (_sync)
            {
                if (!_resumed)
                {
                    _pending.Enqueue((callback, state));
                    _firstPost.TrySetResult();
                    return;
                }
            }
            ThreadPool.QueueUserWorkItem(_ => callback(state));
        }

        public T Run<T>(Func<T> action)
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try
            {
                return action();
            }
            finally
            {
                SetSynchronizationContext(previous);
            }
        }

        public void RunPending()
        {
            Run(() =>
            {
                while (true)
                {
                    (SendOrPostCallback Callback, object? State) pending;
                    lock (_sync)
                    {
                        if (!_pending.TryDequeue(out pending))
                            return true;
                    }
                    pending.Callback(pending.State);
                }
            });
        }

        public void Resume()
        {
            lock (_sync)
                _resumed = true;
            RunPending();
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"AniMeido-idle-drain-{Guid.NewGuid():N}");
        private readonly HttpClient _http = new(new RejectNetworkHandler());
        private readonly PlaybackDiagnosticRecorder _diagnostics;

        public Fixture()
        {
            _diagnostics = new PlaybackDiagnosticRecorder(_root);
            Resolver = new WebMediaResolver(_http,
                new PlayerRuntimeSettingsStore(Path.Combine(_root, "runtime.json")),
                new HostWebSessionManager(_root), _diagnostics);
        }

        public WebMediaResolver Resolver { get; }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Resolver.DisposeAsync().AsTask().WaitAsync(Budget);
            }
            finally
            {
                _http.Dispose();
                await _diagnostics.DisposeAsync();
                if (Directory.Exists(_root))
                    Directory.Delete(_root, recursive: true);
            }
        }
    }

    private sealed class RejectNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("该测试不应发送 HTTP 请求。");
    }
}
