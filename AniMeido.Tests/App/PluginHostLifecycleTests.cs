using AniMeido.App.Services;
using AniMeido.Contracts.Playback;
using AniMeido.Contracts.PersonalAnime;
using AniMeido.PluginProtocol;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Text.Json;

namespace AniMeido.Tests;

public sealed class PluginHostLifecycleTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(-1, false)]
    public void ExitClassifier_DistinguishesNormalWindowClose(
        int exitCode,
        bool expected)
        => Assert.Equal(
            expected,
            PluginHostExitClassifier.IsNormal(exitCode));

    [Fact]
    public void ManifestSnapshot_ContainsOnlyRequestedPlugin()
    {
        var manifest = new PluginManifest
        {
            PluginId = "AniMeido.Plugin.Player",
            DisplayName = "在线播放器",
            Contributions = new PluginContributions
            {
                Commands =
                [
                    new PluginCommandContribution
                    {
                        Id = "open",
                        Title = "打开播放器",
                        Icon = "\uE768",
                    },
                ],
                Navigation =
                [
                    new PluginNavigationContribution
                    {
                        Command = "open",
                    },
                ],
                Settings =
                [
                    new PluginSettingsContribution
                    {
                        Id = "player.settings",
                        Title = "播放源",
                        Icon = "\uE713",
                    },
                ],
                Capabilities =
                [
                    PluginHostProtocol.AnimePlaybackCapability,
                ],
            },
        };

        var snapshot = PluginHostSession.CreateManifestSnapshot(manifest);

        var command = Assert.Single(snapshot.NavigationCommands);
        Assert.Equal(manifest.PluginId, command.PluginId);
        var settings = Assert.Single(snapshot.Settings);
        Assert.Equal(manifest.PluginId, settings.PluginId);
        Assert.Contains(
            PluginHostProtocol.AnimePlaybackCapability,
            snapshot.Capabilities);
        Assert.Empty(snapshot.Failures);
    }

    [Fact]
    public async Task PersonalAnimeCallback_RequiresDeclaredCapability()
    {
        var gateway = new RecordingPersonalAnimeGateway();
        var target = new PersonalAnimeCallbackRpcTarget(
            gateway,
            new PluginManifest { PluginId = "sample.plugin" });
        var request = CreateCallbackRequest(
            nameof(PersonalAnimeCallbackRpcTarget.QuerySelectionAsync),
            new PersonalAnimeSelectionQuery());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            target.DispatchAsync(request, CancellationToken.None));
        Assert.Equal(0, gateway.QueryCount);

        var writeRequest = CreateCallbackRequest(
            nameof(PersonalAnimeCallbackRpcTarget.ApplyChangesAsync),
            new PersonalAnimeChangeSet("sample.plugin", []));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            target.DispatchAsync(writeRequest, CancellationToken.None));
        Assert.Equal(0, gateway.ApplyCount);
    }

    [Fact]
    public async Task PersonalAnimeCallback_PropagatesTokenAndRequiresExactWriteSource()
    {
        var gateway = new RecordingPersonalAnimeGateway();
        var target = new PersonalAnimeCallbackRpcTarget(
            gateway,
            new PluginManifest
            {
                PluginId = "sample.plugin",
                Contributions = new PluginContributions
                {
                    Capabilities = [
                        PluginHostProtocol.PersonalAnimeDataCapability,
                    ],
                },
            });
        using var cancellation = new CancellationTokenSource();
        var queryRequest = CreateCallbackRequest(
            nameof(PersonalAnimeCallbackRpcTarget.QuerySelectionAsync),
            new PersonalAnimeSelectionQuery());

        await target.DispatchAsync(queryRequest, cancellation.Token);

        Assert.Equal(cancellation.Token, gateway.LastCancellationToken);
        var contextRequest = CreateCallbackRequest(
            nameof(PersonalAnimeCallbackRpcTarget.BuildContextAsync),
            new PersonalAnimeContextRequest(
                "test",
                [],
                PersonalAnimeDataCategory.None));
        await target.DispatchAsync(contextRequest, cancellation.Token);
        Assert.Equal(cancellation.Token, gateway.LastCancellationToken);
        var mismatchedWrite = CreateCallbackRequest(
            nameof(PersonalAnimeCallbackRpcTarget.ApplyChangesAsync),
            new PersonalAnimeChangeSet("SAMPLE.PLUGIN", []));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            target.DispatchAsync(mismatchedWrite, CancellationToken.None));
        Assert.Equal(0, gateway.ApplyCount);

        var matchingWrite = CreateCallbackRequest(
            nameof(PersonalAnimeCallbackRpcTarget.ApplyChangesAsync),
            new PersonalAnimeChangeSet("sample.plugin", []));
        await target.DispatchAsync(matchingWrite, cancellation.Token);
        Assert.Equal(1, gateway.ApplyCount);
        Assert.Equal(cancellation.Token, gateway.LastCancellationToken);
    }

    [Fact]
    public async Task TeardownBudget_DefersCancellationSourceDisposalUntilTaskEnds()
    {
        using var cancellation = new CancellationTokenSource();
        var taskCompletion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var budget = new CancellationTokenSource();
        budget.Cancel();

        var result = await PluginHostTaskTeardown.CancelAndAwaitAsync(
            cancellation,
            taskCompletion.Task,
            budget.Token);

        try
        {
            Assert.False(result.Completed);
            using (cancellation.Token.Register(static () => { }))
            {
            }

            taskCompletion.SetResult();
            await result.DeferredCleanup.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Throws<ObjectDisposedException>(() =>
                cancellation.Token.Register(static () => { }));
        }
        finally
        {
            taskCompletion.TrySetResult();
            await Task.WhenAny(result.DeferredCleanup).WaitAsync(TimeSpan.FromSeconds(5));
            await result.DeferredCleanup.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    [Fact]
    public async Task TeardownBudget_ObservesDeferredFaults()
    {
        using var cancellation = new CancellationTokenSource();
        var taskCompletion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var budget = new CancellationTokenSource();
        budget.Cancel();

        var result = await PluginHostTaskTeardown.CancelAndAwaitAsync(
            cancellation,
            taskCompletion.Task,
            budget.Token,
            observed.SetResult);

        taskCompletion.SetException(new InvalidOperationException("fixture"));
        await result.DeferredCleanup.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsType<InvalidOperationException>(
            await observed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    [Trait("Category", "Mechanism")]
    // 持有私有生命周期门，确定性验证并发 Dispose 共享尚未完成的任务。
    public async Task Session_DisposeSharesCompletionAndKeepsGateUsable()
    {
        await using var session = CreateSession();
        var gate = GetSessionGate(session);
        var ownsGate = false;
        try
        {
            await gate.WaitAsync();
            ownsGate = true;
            var first = session.DisposeAsync().AsTask();
            var second = session.DisposeAsync().AsTask();

            Assert.Same(first, second);
            Assert.False(first.IsCompleted);
            gate.Release();
            ownsGate = false;

            await first.WaitAsync(TimeSpan.FromSeconds(5));
            await second.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(session.IsRunning);
        }
        finally
        {
            if (ownsGate)
            {
                gate.Release();
            }
        }
    }

    [Fact]
    [Trait("Category", "Mechanism")]
    // 持有私有生命周期门，确保 Stop 尚未结束时 Start 被拒绝，而非依赖调度时序。
    public async Task Session_StartIsRejectedWhileStopOwnsLifecycle()
    {
        await using var session = CreateSession();
        var gate = GetSessionGate(session);
        var ownsGate = false;
        try
        {
            await gate.WaitAsync();
            ownsGate = true;
            var stopping = session.StopAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                session.StartAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            gate.Release();
            ownsGate = false;
            await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            if (ownsGate)
            {
                gate.Release();
            }
        }
    }

    [Fact]
    [Trait("Category", "Mechanism")]
    // 持有私有生命周期门，把两次启动固定在排队阶段以验证独立取消。
    public async Task Session_QueuedStartsCancelIndependentlyBeforeGateOwnership()
    {
        await using var session = CreateSession();
        var gate = GetSessionGate(session);
        var ownsGate = false;
        using var firstCancellation = new CancellationTokenSource();
        using var secondCancellation = new CancellationTokenSource();
        try
        {
            await gate.WaitAsync();
            ownsGate = true;
            var first = session.StartAsync(firstCancellation.Token);
            var second = session.StartAsync(secondCancellation.Token);
            firstCancellation.Cancel();
            secondCancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                first.WaitAsync(TimeSpan.FromSeconds(5)));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                second.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            if (ownsGate)
            {
                gate.Release();
            }
        }
    }

    [Fact]
    [Trait("Category", "Mechanism")]
    // 私有门及延迟清理任务是本用例要检查的预算/资源机制，IsRunning 无法证明延迟清理已结束。
    public async Task Session_StopBudgetReturnsBoundedFailureAndDefersGateCleanup()
    {
        await using var session = CreateSession();
        var gate = GetSessionGate(session);
        var ownsGate = false;
        try
        {
            await gate.WaitAsync();
            ownsGate = true;
            var stopping = session.StopAsync();

            var waitFailure = await Record.ExceptionAsync(() =>
                stopping.WaitAsync(TimeSpan.FromSeconds(7)));
            Assert.NotNull(waitFailure);
            Assert.True(stopping.IsCompleted);
            Assert.True(stopping.IsFaulted);
            var productionFailure = Assert.IsType<TimeoutException>(
                stopping.Exception!.InnerException);
            Assert.Contains("5 second budget", productionFailure.Message);
            Assert.False(session.IsRunning);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                session.StartAsync().WaitAsync(TimeSpan.FromSeconds(5)));

            var deferred = GetSessionDeferredCleanup(session);
            Assert.NotNull(deferred);
            Assert.False(deferred!.IsCompleted);
            gate.Release();
            ownsGate = false;
            await deferred.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<FileNotFoundException>(() =>
                session.StartAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            if (ownsGate)
            {
                gate.Release();
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [Trait("Category", "Mechanism")]
    // 在生命周期门内模拟停止预算已用尽，子进程由测试自己的 stdin 门闩控制。
    public async Task Session_KeepsOwnershipUntilAnUnconfirmedProcessActuallyExits(
        int disposed)
    {
        await using var child = await WaitingChildProcess.StartAsync();
        var session = CreateSession();
        using var sessionProcess = Process.GetProcessById(child.Process.Id);
        using var pipe = new NamedPipeServerStream(
            $"AniMeido.N077.Command.{Guid.NewGuid():N}", PipeDirection.InOut);
        using var callbackPipe = new NamedPipeServerStream(
            $"AniMeido.N077.Callback.{Guid.NewGuid():N}", PipeDirection.InOut);
        var pipeHandle = pipe.SafePipeHandle;
        var callbackHandle = callbackPipe.SafePipeHandle;
        var exitNotifications = 0;
        session.Exited += (_, _) => Interlocked.Increment(ref exitNotifications);
        Task? queuedExit = null;
        Task? processExit = null;
        try
        {
            var gate = GetSessionGate(session);
            TimeoutException failure;
            await gate.WaitAsync();
            try
            {
                SetSessionField(session, "_process", sessionProcess);
                SetSessionField(session, "_pipe", pipe);
                SetSessionField(session, "_callbackPipe", callbackPipe);
                SetSessionField(session, "_disposed", disposed);
                queuedExit = Assert.IsAssignableFrom<Task>(typeof(PluginHostSession)
                    .GetMethod("HandleProcessExitedAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(session, [sessionProcess]));
                Assert.False(queuedExit.IsCompleted);

                var invocation = Assert.Throws<TargetInvocationException>(() =>
                    typeof(PluginHostSession)
                        .GetMethod("CleanupConnection", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(session, null));
                failure = Assert.IsType<TimeoutException>(invocation.InnerException);
                Assert.Contains(child.Process.Id.ToString(), failure.Message);
                Assert.Contains(session.PluginId, failure.Message);
                Assert.Same(sessionProcess, GetSessionField(session, "_process"));
                processExit = Assert.IsAssignableFrom<Task>(
                    GetSessionField(session, "_processExitTask"));
                Assert.False(processExit.IsCompleted);
                Assert.Null(GetSessionField(session, "_pipe"));
                Assert.Null(GetSessionField(session, "_callbackPipe"));
                Assert.True(pipeHandle.IsClosed);
                Assert.True(callbackHandle.IsClosed);
            }
            finally
            {
                gate.Release();
            }

            await queuedExit.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, Volatile.Read(ref exitNotifications));
            var first = disposed == 0
                ? session.StopAsync()
                : session.DisposeAsync().AsTask();
            var second = disposed == 0
                ? session.StopAsync()
                : session.DisposeAsync().AsTask();
            Assert.Same(first, second);
            Assert.Same(failure, await Assert.ThrowsAsync<TimeoutException>(() =>
                first.WaitAsync(TimeSpan.FromSeconds(5))));
            Assert.False(child.Process.HasExited);
            Assert.False(processExit.IsCompleted);
            Assert.Same(sessionProcess, GetSessionField(session, "_process"));

            await child.ReleaseAsync();
            await processExit.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(GetSessionField(session, "_process"));
            Assert.Equal(0, Volatile.Read(ref exitNotifications));
        }
        finally
        {
            await child.DisposeAsync();
            if (queuedExit is not null)
                await ObserveSessionTeardownAsync(queuedExit);
            if (processExit is not null)
                await ObserveSessionTeardownAsync(processExit);
            await ObserveSessionTeardownAsync(session.DisposeAsync().AsTask());
        }
    }

    private static object? GetSessionField(PluginHostSession session, string name)
        => typeof(PluginHostSession)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(session);

    private static void SetSessionField(PluginHostSession session, string name, object? value)
        => typeof(PluginHostSession)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(session, value);

    private static async Task ObserveSessionTeardownAsync(Task operation)
    {
        try
        {
            await operation.WaitAsync(TimeSpan.FromSeconds(5));
        }
#pragma warning disable CA1031 // Assertions own failures; cleanup observes every registered task.
        catch (Exception) when (operation.IsCompleted)
        {
        }
#pragma warning restore CA1031
    }

    private sealed class WaitingChildProcess : IAsyncDisposable
    {
        private readonly Task<string> _errors;
        private Task? _disposeTask;

        private WaitingChildProcess(Process process)
        {
            Process = process;
            _errors = process.StandardError.ReadToEndAsync();
        }

        public Process Process { get; }

        public static async Task<WaitingChildProcess> StartAsync()
        {
            var start = new ProcessStartInfo("pwsh")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-Command");
            start.ArgumentList.Add(
                "[Console]::Out.WriteLine('ready'); [Console]::Out.Flush(); [void][Console]::In.ReadLine()");
            var child = new WaitingChildProcess(
                System.Diagnostics.Process.Start(start)
                    ?? throw new InvalidOperationException("The test child process did not start."));
            try
            {
                Assert.Equal("ready", await child.Process.StandardOutput.ReadLineAsync()
                    .WaitAsync(TimeSpan.FromSeconds(5)));
                return child;
            }
            catch
            {
                await child.DisposeAsync();
                throw;
            }
        }

        public async Task ReleaseAsync()
        {
            if (!Process.HasExited)
            {
                await Process.StandardInput.WriteLineAsync("exit");
                await Process.StandardInput.FlushAsync();
            }

            await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }

        public ValueTask DisposeAsync()
            => new(_disposeTask ??= DisposeCoreAsync());

        private async Task DisposeCoreAsync()
        {
            try
            {
                try
                {
                    await ReleaseAsync();
                }
                catch (IOException) when (Process.HasExited)
                {
                }
                catch (TimeoutException)
                {
                    if (!Process.HasExited)
                    {
                        Process.Kill(entireProcessTree: true);
                    }

                    await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }

                await _errors.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                Process.Dispose();
            }
        }
    }

    private static PluginHostSession CreateSession()
        => new(
            new HostedPluginDescriptor(
                "fixture",
                new PluginManifest
                {
                    PluginId = "fixture.plugin",
                    DisplayName = "Fixture",
                }),
            "missing-host.exe",
            new NoOpPlaybackProgressSink(),
            new RecordingPersonalAnimeGateway(),
            NullLogger.Instance);

    private static SemaphoreSlim GetSessionGate(PluginHostSession session)
        => (SemaphoreSlim)typeof(PluginHostSession)
            .GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(session)!;

    private static Task? GetSessionDeferredCleanup(PluginHostSession session)
        => (Task?)typeof(PluginHostSession)
            .GetField(
                "_deferredCleanupTask",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(session);

    private static JsonPipeRpcRequest CreateCallbackRequest<T>(
        string method,
        T argument)
        => new(
            1,
            method,
            [JsonSerializer.SerializeToElement(argument)]);

    private sealed class RecordingPersonalAnimeGateway
        : IPersonalAnimeDataGateway
    {
        public int QueryCount { get; private set; }

        public int ApplyCount { get; private set; }

        public CancellationToken LastCancellationToken { get; private set; }

        public Task<IReadOnlyList<PersonalAnimeSelectionItem>>
            QuerySelectionAsync(
                PersonalAnimeSelectionQuery query,
                CancellationToken cancellationToken = default)
        {
            _ = query;
            QueryCount++;
            LastCancellationToken = cancellationToken;
            return Task.FromResult<IReadOnlyList<PersonalAnimeSelectionItem>>(
                []);
        }

        public Task<PersonalAnimeContextSnapshot> BuildContextAsync(
            PersonalAnimeContextRequest request,
            CancellationToken cancellationToken = default)
        {
            _ = request;
            LastCancellationToken = cancellationToken;
            return Task.FromResult(new PersonalAnimeContextSnapshot(
                "snapshot",
                "test",
                PersonalAnimeDataCategory.None,
                DateTimeOffset.UnixEpoch,
                [],
                [],
                []));
        }

        public Task<PersonalAnimeChangeApplyResult> ApplyChangesAsync(
            PersonalAnimeChangeSet changeSet,
            CancellationToken cancellationToken = default)
        {
            _ = changeSet;
            ApplyCount++;
            LastCancellationToken = cancellationToken;
            return Task.FromResult(new PersonalAnimeChangeApplyResult([]));
        }
    }

    private sealed class NoOpPlaybackProgressSink : IAnimePlaybackProgressSink
    {
        public Task RecordAsync(
            AnimePlaybackProgress progress,
            CancellationToken cancellationToken = default)
        {
            _ = progress;
            _ = cancellationToken;
            return Task.CompletedTask;
        }
    }
}
