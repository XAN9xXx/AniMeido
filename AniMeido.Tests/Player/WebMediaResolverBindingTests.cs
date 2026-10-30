using AniMeido.Plugin.Player.Diagnostics;
using AniMeido.Plugin.Player.Services;
using AniMeido.Plugin.Player.Sources.Web;
using System.Reflection;

namespace AniMeido.Tests;

public sealed class WebMediaResolverBindingTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);
    private static readonly FieldInfo ResolverGate = typeof(WebMediaResolver)
        .GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo IdleCancellation = typeof(WebMediaResolver)
        .GetField("_idleCleanupCancellation", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Theory]
    [InlineData("same")]
    [InlineData("closed")]
    [InlineData("old-detach")]
    [Trait("Category", "Mechanism")]
    public async Task NoBindingChangePreservesTheCurrentPlayersIdleCleanup(string request)
    {
        await using var fixture = new Fixture();
        var player = Binding(1);
        var temporary = Binding(2);
        Assert.True(await fixture.Resolver.AttachUiThreadAsync(player).WaitAsync(Budget));
        await fixture.Resolver.RunWithTemporaryHostAsync(temporary,
            _ => Task.FromResult(true), CancellationToken.None).WaitAsync(Budget);
        var idle = Assert.IsType<CancellationTokenSource>(IdleCancellation.GetValue(fixture.Resolver));
        var idleToken = idle.Token;

        switch (request)
        {
            case "same":
                Assert.True(await fixture.Resolver.AttachUiThreadAsync(player).WaitAsync(Budget));
                break;
            case "closed":
                fixture.Resolver.InvalidateBinding(temporary);
                Assert.False(await fixture.Resolver.AttachUiThreadAsync(temporary).WaitAsync(Budget));
                break;
            case "old-detach":
                await fixture.Resolver.DetachUiThreadAsync(temporary).WaitAsync(Budget);
                break;
        }

        Assert.Same(player, fixture.Resolver.CurrentBinding);
        Assert.False(idleToken.IsCancellationRequested);
        Assert.Same(idle, IdleCancellation.GetValue(fixture.Resolver));
    }

    [Fact]
    [Trait("Category", "Mechanism")]
    public async Task AttachWaitsForTheResolverGateWithoutBlockingTheCaller()
    {
        await using var fixture = new Fixture();
        var binding = Binding(1);
        var gate = Assert.IsType<SemaphoreSlim>(ResolverGate.GetValue(fixture.Resolver));
        await gate.WaitAsync(Budget);
        var gateHeld = true;
        // 独立调用线程可检测被测入口里的同步 Wait；finally 仍能交还锁。
        var invocation = StartAttach(fixture.Resolver, binding);
        Task<bool>? attaching = null;
        try
        {
            attaching = await invocation.WaitAsync(Budget);
            Assert.False(attaching.IsCompleted);
            Assert.Null(fixture.Resolver.CurrentBinding);

            gate.Release();
            gateHeld = false;
            Assert.True(await attaching.WaitAsync(Budget));
            Assert.Same(binding, fixture.Resolver.CurrentBinding);
        }
        finally
        {
            if (gateHeld)
                gate.Release();
            attaching ??= await invocation.WaitAsync(Budget);
            await DrainAsync(attaching);
        }
    }

    [Fact]
    [Trait("Category", "Mechanism")]
    public async Task CancelledAttachDoesNotPublishABindingOrWaitForTheGateToBeReleased()
    {
        await using var fixture = new Fixture();
        var binding = Binding(1);
        var gate = Assert.IsType<SemaphoreSlim>(ResolverGate.GetValue(fixture.Resolver));
        await gate.WaitAsync(Budget);
        using var cancellation = new CancellationTokenSource();
        var invocation = StartAttach(fixture.Resolver, binding, cancellation.Token);
        Task<bool>? attaching = null;
        try
        {
            attaching = await invocation.WaitAsync(Budget);
            Assert.False(attaching.IsCompleted);
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attaching.WaitAsync(Budget));
            Assert.Null(fixture.Resolver.CurrentBinding);
        }
        finally
        {
            cancellation.Cancel();
            gate.Release();
            attaching ??= await invocation.WaitAsync(Budget);
            await DrainAsync(attaching);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TemporaryHostRestoresOnlyThePreviousActiveBinding(bool hasPlayer)
    {
        await using var fixture = new Fixture();
        var player = hasPlayer ? Binding(1) : null;
        var temporary = Binding(2);
        if (player is not null)
            Assert.True(await fixture.Resolver.AttachUiThreadAsync(player).WaitAsync(Budget));

        var result = await fixture.Resolver.RunWithTemporaryHostAsync(
            temporary,
            _ =>
            {
                Assert.Same(temporary, fixture.Resolver.CurrentBinding);
                return Task.FromResult(42);
            }, CancellationToken.None).WaitAsync(Budget);

        Assert.Equal(42, result);
        Assert.Same(player, fixture.Resolver.CurrentBinding);
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "WebView2")));
    }

    [Fact]
    public async Task PlayerCloseInvalidatesImmediatelyAndTemporaryCleanupDoesNotRestoreIt()
    {
        await using var fixture = new Fixture();
        var player = Binding(1);
        var temporary = Binding(2);
        Assert.True(await fixture.Resolver.AttachUiThreadAsync(player).WaitAsync(Budget));
        var entered = NewSignal();
        var release = NewSignal();
        var temporaryOperation = fixture.Resolver.RunWithTemporaryHostAsync(temporary, async token =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(Budget, token);
            return true;
        }, CancellationToken.None);
        Task<Task>? detachInvocation = null;
        Task? detaching = null;
        try
        {
            await entered.Task.WaitAsync(Budget);
            fixture.Resolver.InvalidateBinding(player);
            Assert.False(player.IsActive);
            Assert.Same(temporary, fixture.Resolver.CurrentBinding);

            detachInvocation = StartDetach(fixture.Resolver, player);
            detaching = await detachInvocation.WaitAsync(Budget);
            Assert.False(detaching.IsCompleted);
            release.TrySetResult();

            Assert.True(await temporaryOperation.WaitAsync(Budget));
            await detaching.WaitAsync(Budget);
            Assert.Null(fixture.Resolver.CurrentBinding);
        }
        finally
        {
            release.TrySetResult();
            if (detachInvocation is not null)
                detaching ??= await detachInvocation.WaitAsync(Budget);
            await DrainAsync(temporaryOperation, detaching);
        }
    }

    [Fact]
    [Trait("Category", "Mechanism")]
    public async Task QueuedAttachDoesNotReviveAnOwnerClosedWhileItWasWaiting()
    {
        await using var fixture = new Fixture();
        var binding = Binding(1);
        var gate = Assert.IsType<SemaphoreSlim>(ResolverGate.GetValue(fixture.Resolver));
        await gate.WaitAsync(Budget);
        var gateHeld = true;
        var invocation = StartAttach(fixture.Resolver, binding);
        Task<bool>? attaching = null;
        try
        {
            attaching = await invocation.WaitAsync(Budget);
            Assert.False(attaching.IsCompleted);
            fixture.Resolver.InvalidateBinding(binding);
            Assert.False(binding.IsActive);

            gate.Release();
            gateHeld = false;
            Assert.False(await attaching.WaitAsync(Budget));
            Assert.Null(fixture.Resolver.CurrentBinding);
        }
        finally
        {
            if (gateHeld)
                gate.Release();
            attaching ??= await invocation.WaitAsync(Budget);
            await DrainAsync(attaching);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OldDetachDoesNotRemoveANewerBinding(bool reusedHandle)
    {
        await using var fixture = new Fixture();
        var old = Binding(1);
        var current = Binding(reusedHandle ? 1 : 2);
        Assert.True(await fixture.Resolver.AttachUiThreadAsync(old).WaitAsync(Budget));
        fixture.Resolver.InvalidateBinding(old);
        Assert.True(await fixture.Resolver.AttachUiThreadAsync(current).WaitAsync(Budget));

        await fixture.Resolver.DetachUiThreadAsync(old).WaitAsync(Budget);

        Assert.False(old.IsActive);
        Assert.True(current.IsActive);
        Assert.Same(current, fixture.Resolver.CurrentBinding);
    }

    [Fact]
    public async Task FailedTemporaryOperationStillReleasesItsHostAndRestoresTheActivePlayer()
    {
        await using var fixture = new Fixture();
        var player = Binding(1);
        var temporary = Binding(2);
        Assert.True(await fixture.Resolver.AttachUiThreadAsync(player).WaitAsync(Budget));
        var expected = new IOException("Synthetic temporary operation failure.");

        var actual = await Assert.ThrowsAsync<IOException>(() => fixture.Resolver.RunWithTemporaryHostAsync(
            temporary, _ => Task.FromException<bool>(expected), CancellationToken.None).WaitAsync(Budget));

        Assert.Same(expected, actual);
        Assert.Same(player, fixture.Resolver.CurrentBinding);
    }

    [Fact]
    public async Task CancelledTemporaryOperationStillReleasesItsHostAndRestoresTheActivePlayer()
    {
        await using var fixture = new Fixture();
        var player = Binding(1);
        var temporary = Binding(2);
        Assert.True(await fixture.Resolver.AttachUiThreadAsync(player).WaitAsync(Budget));
        using var cancellation = new CancellationTokenSource();
        var entered = NewSignal();
        var operation = fixture.Resolver.RunWithTemporaryHostAsync(temporary, async token =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return true;
        }, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(Budget);
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(Budget));
            Assert.Same(player, fixture.Resolver.CurrentBinding);
        }
        finally
        {
            cancellation.Cancel();
            await DrainAsync(operation);
        }
    }

    [Fact]
    public async Task FailedTemporaryCleanupKeepsItsBindingUntilItsOwnerDetachesSuccessfully()
    {
        await using var fixture = new Fixture();
        var player = Binding(1);
        var expected = new IOException("Synthetic UI cleanup failure.");
        var failCleanup = true;
        var temporary = Binding(2, (action, token) => failCleanup
            ? Task.FromException(expected)
            : RunInlineAsync(action, token));
        Assert.True(await fixture.Resolver.AttachUiThreadAsync(player).WaitAsync(Budget));
        try
        {
            var actual = await Assert.ThrowsAsync<IOException>(() => fixture.Resolver.RunWithTemporaryHostAsync(
                temporary, _ => Task.FromResult(true), CancellationToken.None).WaitAsync(Budget));

            Assert.Same(expected, actual);
            Assert.Same(temporary, fixture.Resolver.CurrentBinding);
            failCleanup = false;
            await fixture.Resolver.DetachUiThreadAsync(temporary).WaitAsync(Budget);
            Assert.False(temporary.IsActive);
            Assert.Null(fixture.Resolver.CurrentBinding);
        }
        finally
        {
            failCleanup = false;
            await fixture.Resolver.DetachUiThreadAsync(temporary).WaitAsync(Budget);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourceManagementStopWaitsThroughTheWholeTemporaryHostCleanup(bool operationFails)
    {
        await using var fixture = new Fixture();
        var session = new SourceManagementSession();
        var cookiesEntered = NewSignal();
        var releaseCookies = NewSignal();
        var metadataEntered = NewSignal();
        var releaseMetadata = NewSignal();
        var cleanupEntered = NewSignal();
        var releaseCleanup = NewSignal();
        var failure = new IOException("Synthetic metadata operation failure.");
        var temporary = Binding(1, async (action, token) =>
        {
            cleanupEntered.TrySetResult();
            await releaseCleanup.Task.WaitAsync(Budget);
            await RunInlineAsync(action, token);
        });
        async Task ClearAsync()
        {
            using var write = session.TryBeginWrite();
            Assert.NotNull(write);
            Assert.Equal(CancellationToken.None, write.Token);
            await fixture.Resolver.RunWithTemporaryHostAsync(temporary, async token =>
            {
                cookiesEntered.TrySetResult();
                await releaseCookies.Task.WaitAsync(Budget, token);
                Assert.False(token.IsCancellationRequested);
                metadataEntered.TrySetResult();
                await releaseMetadata.Task.WaitAsync(Budget, token);
                if (operationFails)
                    throw failure;
                return true;
            }, write.Token);
        }

        var clearing = ClearAsync();
        Task? stopping = null;
        try
        {
            await cookiesEntered.Task.WaitAsync(Budget);
            stopping = session.StopAsync();
            Assert.False(stopping.IsCompleted);
            Assert.Null(session.TryBeginWrite());

            releaseCookies.TrySetResult();
            await metadataEntered.Task.WaitAsync(Budget);
            Assert.False(stopping.IsCompleted);

            releaseMetadata.TrySetResult();
            await cleanupEntered.Task.WaitAsync(Budget);
            Assert.False(clearing.IsCompleted);
            Assert.False(stopping.IsCompleted);
            Assert.Same(temporary, fixture.Resolver.CurrentBinding);

            releaseCleanup.TrySetResult();
            if (operationFails)
            {
                var actual = await Assert.ThrowsAsync<IOException>(() => clearing.WaitAsync(Budget));
                Assert.Same(failure, actual);
            }
            else
            {
                await clearing.WaitAsync(Budget);
            }
            await stopping.WaitAsync(Budget);
            Assert.Null(fixture.Resolver.CurrentBinding);
            Assert.False(Directory.Exists(Path.Combine(fixture.Root, "WebView2")));
        }
        finally
        {
            releaseCookies.TrySetResult();
            releaseMetadata.TrySetResult();
            releaseCleanup.TrySetResult();
            await DrainAsync(clearing);
            await (stopping ?? session.StopAsync()).WaitAsync(Budget);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManagerCloseCancelsPlaybackBeforeWaitingForSettingsAndItsAcceptedCleanup(bool clearingSession)
    {
        await using var fixture = new Fixture();
        var player = Binding(1);
        var settings = Binding(2);
        var session = new SourceManagementSession();
        using var cancellation = new CancellationTokenSource();
        var resolutionEntered = NewSignal();
        var resolutionCancelled = NewSignal();
        var releaseResolution = NewSignal();
        var cleanupEntered = NewSignal();
        var releaseCleanup = NewSignal();
        Assert.True(await fixture.Resolver.AttachUiThreadAsync(player).WaitAsync(Budget));
        var resolving = fixture.Resolver.RunWithTemporaryHostAsync(player, async token =>
        {
            using var registration = token.Register(() => resolutionCancelled.TrySetResult());
            resolutionEntered.TrySetResult();
            await releaseResolution.Task;
            token.ThrowIfCancellationRequested();
            return true;
        }, cancellation.Token);
        Task clearing = Task.CompletedTask;
        Task? closing = null;
        var playerCloseStarted = false;
        try
        {
            await resolutionEntered.Task.WaitAsync(Budget);
            if (clearingSession)
                clearing = ClearSessionAsync();

            closing = PlayerWindowManager.CloseWindowsAsync(CloseSettingsAsync, ClosePlayerAsync);
            await resolutionCancelled.Task.WaitAsync(Budget);
            Assert.True(playerCloseStarted);
            Assert.False(closing.IsCompleted);
            Assert.Null(session.TryBeginRead());
            Assert.Null(session.TryBeginWrite());

            releaseResolution.TrySetResult();
            if (clearingSession)
            {
                await cleanupEntered.Task.WaitAsync(Budget);
                Assert.False(closing.IsCompleted);
                releaseCleanup.TrySetResult();
            }
            await closing.WaitAsync(Budget);
            Assert.Null(fixture.Resolver.CurrentBinding);
        }
        finally
        {
            cancellation.Cancel();
            releaseResolution.TrySetResult();
            releaseCleanup.TrySetResult();
            await DrainAsync(resolving, clearing, closing);
            await session.StopAsync().WaitAsync(Budget);
        }

        async Task ClearSessionAsync()
        {
            using var write = session.TryBeginWrite();
            Assert.NotNull(write);
            await fixture.Resolver.RunWithTemporaryHostAsync(settings, async _ =>
            {
                cleanupEntered.TrySetResult();
                await releaseCleanup.Task;
                return true;
            }, write.Token);
        }

        async Task CloseSettingsAsync()
        {
            await session.StopAsync();
            fixture.Resolver.InvalidateBinding(settings);
            await fixture.Resolver.DetachUiThreadAsync(settings);
        }

        async Task ClosePlayerAsync()
        {
            playerCloseStarted = true;
            fixture.Resolver.InvalidateBinding(player);
            cancellation.Cancel();
            try
            {
                await resolving;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
            await fixture.Resolver.DetachUiThreadAsync(player);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManagerCloseStartsThePlayerAfterSettingsFailureAndWaitsForBothResults(bool synchronousFailure)
    {
        var settingsFailure = new IOException("settings close failed");
        var playerFailure = new InvalidOperationException("player close failed");
        var playerCompletion = NewSignal();
        var playerCloseStarted = false;
        var closing = PlayerWindowManager.CloseWindowsAsync(CloseSettingsAsync, () =>
        {
            playerCloseStarted = true;
            return playerCompletion.Task;
        });
        try
        {
            Assert.True(playerCloseStarted);
            Assert.False(closing.IsCompleted);
            playerCompletion.TrySetException(playerFailure);
            var failure = await Assert.ThrowsAsync<AggregateException>(() => closing.WaitAsync(Budget));
            Assert.Equal(2, failure.Flatten().InnerExceptions.Count);
            Assert.Contains(settingsFailure, failure.Flatten().InnerExceptions);
            Assert.Contains(playerFailure, failure.Flatten().InnerExceptions);
        }
        finally
        {
            playerCompletion.TrySetResult();
            await DrainAsync(closing, playerCompletion.Task);
        }

        Task CloseSettingsAsync()
        {
            if (synchronousFailure)
                throw settingsFailure;
            return Task.FromException(settingsFailure);
        }
    }

    private static Task<Task<bool>> StartAttach(
        WebMediaResolver resolver, WebViewHostBinding binding, CancellationToken token = default)
        => Task.Factory.StartNew(() => resolver.AttachUiThreadAsync(binding, token),
            CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);

    private static Task<Task> StartDetach(WebMediaResolver resolver, WebViewHostBinding binding)
        => Task.Factory.StartNew(() => resolver.DetachUiThreadAsync(binding),
            CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);

    private static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static WebViewHostBinding Binding(
        int handle, Func<Func<Task>, CancellationToken, Task>? dispatch = null)
        => new((nint)handle, dispatch ?? RunInlineAsync);

    private static async Task RunInlineAsync(Func<Task> action, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await action();
    }

    private static async Task DrainAsync(params Task?[] operations)
    {
        // 结果断言留在正常路径；只观察真实任务结果，等待超时本身仍须报告。
        async Task ObserveAsync() => await Task.WhenAll(operations.OfType<Task>()).ConfigureAwait(
            ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        await ObserveAsync().WaitAsync(Budget);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly HttpClient _http = new(new RejectNetworkHandler());
        private readonly PlaybackDiagnosticRecorder _diagnostics;
        public string Root { get; } = Path.Combine(
            Path.GetTempPath(), $"AniMeido-web-binding-{Guid.NewGuid():N}");
        public WebMediaResolver Resolver { get; }

        public Fixture()
        {
            _diagnostics = new PlaybackDiagnosticRecorder(Root);
            Resolver = new WebMediaResolver(_http,
                new PlayerRuntimeSettingsStore(Path.Combine(Root, "runtime.json")),
                new HostWebSessionManager(Root), _diagnostics);
        }

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
                if (Directory.Exists(Root))
                    Directory.Delete(Root, recursive: true);
            }
        }
    }

    private sealed class RejectNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("该测试不应发送 HTTP 请求。");
    }
}
