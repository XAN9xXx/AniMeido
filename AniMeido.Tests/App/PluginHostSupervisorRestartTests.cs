using AniMeido.App.Services;
using AniMeido.Contracts.Playback;
using AniMeido.Contracts.PersonalAnime;
using AniMeido.PluginProtocol;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace AniMeido.Tests;

public sealed partial class PluginHostSupervisorRestartTests : IDisposable
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"AniMeido-restart-{Guid.NewGuid():N}");

    [Theory]
    [InlineData("fixture.plugin")]
    [InlineData("FIXTURE.PLUGIN")]
    public async Task Activation_ReusesSessionForTheSameIdRegardlessOfCase(string pluginId)
    {
        var fixture = await CreateAsync();
        await using var supervisor = fixture.Supervisor;
        var original = Assert.Single(fixture.Sessions);
        var startConfirmations = original.StartCount;
        var commands = original.CommandCount;

        await supervisor.InvokeCommandAsync(pluginId, "fixture.plugin.open").WaitAsync(Budget);

        Assert.Same(original, Assert.Single(fixture.Sessions));
        // 假会话统计启动确认调用；真实会话已运行时直接复用进程，不等于新增进程。
        Assert.Equal(startConfirmations + 1, original.StartCount);
        Assert.Equal(commands + 1, original.CommandCount);
        Assert.Equal(0, original.DisposeCount);
        Assert.True(supervisor.IsRunning);
        Assert.Empty(fixture.Logger.Warnings);
    }

    [Fact]
    public async Task AbnormalExit_RestartsOnceThenRequiresManualReload()
    {
        var fixture = await CreateAsync();
        await using var supervisor = fixture.Supervisor;
        var session = fixture.Sessions.Single();
        var statuses = new ConcurrentQueue<string>();
        supervisor.StatusChanged += (_, _) => statuses.Enqueue(supervisor.StatusText);
        await StatusAsync(supervisor, "Fixture 运行中", () => session.Exit(7));
        Assert.Equal(2, session.StartCount);
        Assert.Equal(new[] { "Fixture 异常退出，正在自动恢复", "Fixture 运行中" }, statuses);
        await StatusAsync(supervisor, "Fixture 连续异常退出，请手动重载", () => session.Exit(8));
        Assert.Equal(2, session.StartCount);
        Assert.False(supervisor.IsRunning);
    }

    [Fact]
    public async Task NormalExit_DoesNotRestartAndResetsRecoveryBudget()
    {
        var fixture = await CreateAsync();
        await using var supervisor = fixture.Supervisor;
        var session = fixture.Sessions.Single();
        await StatusAsync(supervisor, "Fixture 运行中", () => session.Exit(1));
        Assert.True(PluginHostExitClassifier.IsNormal(0));
        await StatusAsync(supervisor, "Fixture 已关闭，用到时会自动启动", () => session.Exit(0));
        Assert.Equal(2, session.StartCount);
        await StatusAsync(supervisor, "Fixture 运行中", () => session.Exit(2));
        Assert.Equal(3, session.StartCount);
    }

    [Fact]
    [Trait("Category", "Mechanism")]
    // 保留恢复阶段日志标识的检查，并核对失败日志级别。
    public async Task RestartException_IsErrorAndConsumesBudgetWithoutEscapingEvent()
    {
        var fixture = await CreateAsync();
        await using var supervisor = fixture.Supervisor;
        var session = fixture.Sessions.Single();
        var failure = new IOException("fixture restart failure");
        session.NextStart = _ => Task.FromException<PluginHostSnapshot>(failure);
        await StatusAsync(supervisor, "Fixture 自动恢复失败：fixture restart failure", () => session.Exit(1));
        var warning = Assert.Single(fixture.Logger.Warnings);
        Assert.Same(failure, warning.Exception);
        Assert.Equal(LogLevel.Error, warning.Level);
        Assert.Contains("automatic restart failed", warning.Message);
        await StatusAsync(supervisor, "Fixture 连续异常退出，请手动重载", () => session.Exit(1));
        Assert.Equal(2, session.StartCount);
    }

    [Fact]
    public async Task RestartFailureSnapshot_PublishesLoadFailureAndConsumesBudget()
    {
        var fixture = await CreateAsync();
        await using var supervisor = fixture.Supervisor;
        var session = fixture.Sessions.Single();
        session.NextStart = _ => Task.FromResult(new PluginHostSnapshot([], [], [],
            [new HostedPluginFailure(session.PluginId, "fixture load failure")]));
        await StatusAsync(supervisor, "Fixture 加载失败：fixture load failure", () => session.Exit(1));
        Assert.Equal(2, session.StartCount);
        Assert.Contains(fixture.Logger.Warnings, item => item.Message.Contains("fixture load failure", StringComparison.Ordinal));
        await StatusAsync(supervisor, "Fixture 连续异常退出，请手动重载", () => session.Exit(1));
        Assert.Equal(2, session.StartCount);
    }

    [Fact]
    public async Task UserActivationSuccess_ResetsRecoveryBudget()
    {
        var fixture = await CreateAsync();
        await using var supervisor = fixture.Supervisor;
        var session = fixture.Sessions.Single();
        await ExhaustAsync(supervisor, session);
        await supervisor.InvokeCommandAsync(session.PluginId, "fixture.plugin.open").WaitAsync(Budget);
        Assert.Equal(3, session.StartCount);
        Assert.Equal(2, session.CommandCount);
        await StatusAsync(supervisor, "Fixture 运行中", () => session.Exit(1));
        Assert.Equal(4, session.StartCount);
    }

    [Fact]
    [Trait("Category", "Mechanism")]
    // 直接检查 Reload 清空预算：随后公开的用户启动也会清空它，无法等力度验证 Reload 本身。
    public async Task Reload_ResetsRecoveryBudgetAndReplacesSession()
    {
        var fixture = await CreateAsync();
        await using var supervisor = fixture.Supervisor;
        var first = fixture.Sessions.Single();
        await ExhaustAsync(supervisor, first);
        Assert.Contains(first.PluginId, RecoveryBudget(supervisor));
        await supervisor.ReloadAsync().WaitAsync(Budget);
        Assert.Equal(1, first.DisposeCount);
        // 尚未再次激活：独立证明 Reload 清空预算，而不是被后续用户启动的 reset=true 掩盖。
        Assert.Empty(RecoveryBudget(supervisor));
        await supervisor.InvokeCommandAsync(first.PluginId, "fixture.plugin.open").WaitAsync(Budget);
        var replacement = fixture.Sessions.Last();
        Assert.NotSame(first, replacement);
        await StatusAsync(supervisor, "Fixture 运行中", () => replacement.Exit(1));
        Assert.Equal(2, replacement.StartCount);
    }

    [Fact]
    public async Task ExitDuringStopAndAfterDispose_IsIgnoredEvenForQueuedCallbacks()
    {
        var fixture = await CreateAsync();
        var supervisor = fixture.Supervisor;
        await using (supervisor)
        {
            var session = fixture.Sessions.Single();
            var lateCallback = session.CaptureExitCallback();
            var disposalEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var allowDisposal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            session.OnDispose = async () => { disposalEntered.SetResult(); await allowDisposal.Task.WaitAsync(Budget); };
            var reload = supervisor.ReloadAsync();
            try
            {
                await disposalEntered.Task.WaitAsync(Budget);
                var status = supervisor.StatusText;
                lateCallback();
                Assert.Equal(status, supervisor.StatusText);
                Assert.Equal(1, session.StartCount);
                allowDisposal.SetResult();
                await reload.WaitAsync(Budget);
                await supervisor.DisposeAsync().AsTask().WaitAsync(Budget);
                status = supervisor.StatusText;
                lateCallback();
                Assert.Equal(status, supervisor.StatusText);
                Assert.Equal(1, session.StartCount);
                Assert.False(supervisor.IsRunning);
            }
            finally
            {
                allowDisposal.TrySetResult();
                await Task.WhenAny(reload).WaitAsync(Budget);
                await reload.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
            }
        }
    }

    [Fact]
    public async Task Dispose_CancelsAutomaticRestartBeforeReleasingSession()
    {
        var fixture = await CreateAsync();
        await using var supervisor = fixture.Supervisor;
        var session = fixture.Sessions.Single();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.NextStart = async token =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new PluginHostSnapshot([], [], [], []);
        };

        session.Exit(1);
        await entered.Task.WaitAsync(Budget);
        await supervisor.DisposeAsync().AsTask().WaitAsync(Budget);

        Assert.Equal(1, session.DisposeCount);
        Assert.False(supervisor.IsRunning);
        Assert.Empty(fixture.Logger.Warnings);
    }

    [Fact]
    public async Task Reload_LateRestartResultCannotPublishRunningStatus()
    {
        var fixture = await CreateAsync();
        await using var supervisor = fixture.Supervisor;
        var session = fixture.Sessions.Single();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<PluginHostSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        session.NextStart = _ =>
        {
            entered.SetResult();
            return finish.Task;
        };
        var statuses = new ConcurrentQueue<string>();
        supervisor.StatusChanged += (_, _) => statuses.Enqueue(supervisor.StatusText);

        Task? reload = null;
        try
        {
            session.Exit(1);
            await entered.Task.WaitAsync(Budget);
            reload = supervisor.ReloadAsync();
            finish.SetResult(new PluginHostSnapshot([], [], [], []));
            await reload.WaitAsync(Budget);

            Assert.DoesNotContain("Fixture 运行中", statuses);
            Assert.Equal(1, session.DisposeCount);
            Assert.False(supervisor.IsRunning);
            Assert.DoesNotContain(fixture.Logger.Warnings,
                entry => entry.Level >= LogLevel.Error);
        }
        finally
        {
            finish.TrySetResult(new PluginHostSnapshot([], [], [], []));
            if (reload is not null)
            {
                await reload.WaitAsync(Budget);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopAndDrain_AfterDisposeBudgetWaitsForActualSessionCleanup(bool failCleanup)
    {
        var fixture = await CreateAsync();
        var supervisor = fixture.Supervisor;
        var session = fixture.Sessions.Single();
        var restartEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishRestart = new TaskCompletionSource<PluginHostSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var disposalEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishDisposal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposalFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new IOException("fixture deferred cleanup failure");
        session.NextStart = _ =>
        {
            restartEntered.SetResult();
            return finishRestart.Task;
        };
        session.OnDispose = async () =>
        {
            disposalEntered.SetResult();
            await finishDisposal.Task;
            disposalFinished.SetResult();
            if (failCleanup)
            {
                throw failure;
            }
        };

        try
        {
            session.Exit(1);
            await restartEntered.Task.WaitAsync(Budget);
            fixture.Logger.Warnings.Clear();

            var disposal = supervisor.DisposeAsync().AsTask();
            var timeout = await Assert.ThrowsAsync<TimeoutException>(
                () => disposal.WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.Contains("5 second budget", timeout.Message);
            Assert.Equal(0, session.DisposeCount);

            var firstDrain = supervisor.StopAndDrainAsync();
            var secondDrain = supervisor.StopAndDrainAsync();
            Assert.False(firstDrain.IsCompleted);
            Assert.False(secondDrain.IsCompleted);

            finishRestart.SetResult(new PluginHostSnapshot([], [], [], []));
            await disposalEntered.Task.WaitAsync(Budget);
            Assert.False(firstDrain.IsCompleted);
            Assert.False(secondDrain.IsCompleted);
            Assert.False(disposalFinished.Task.IsCompleted);

            finishDisposal.SetResult();
            if (failCleanup)
            {
                var firstFailure = await Assert.ThrowsAsync<AggregateException>(
                    () => firstDrain.WaitAsync(Budget));
                var secondFailure = await Assert.ThrowsAsync<AggregateException>(
                    () => secondDrain.WaitAsync(Budget));
                var repeatedFailure = await Assert.ThrowsAsync<AggregateException>(
                    () => supervisor.StopAndDrainAsync().WaitAsync(Budget));
                Assert.Same(failure, Assert.Single(firstFailure.InnerExceptions));
                Assert.Same(firstFailure, secondFailure);
                Assert.Same(firstFailure, repeatedFailure);
            }
            else
            {
                await Task.WhenAll(firstDrain, secondDrain).WaitAsync(Budget);
                await supervisor.StopAndDrainAsync().WaitAsync(Budget);
            }

            Assert.True(disposalFinished.Task.IsCompletedSuccessfully);
            Assert.Equal(1, session.DisposeCount);
            Assert.False(supervisor.IsRunning);
            Assert.Same(disposal, supervisor.DisposeAsync().AsTask());
            Assert.Empty(fixture.Logger.Warnings);
        }
        finally
        {
            finishRestart.TrySetResult(new PluginHostSnapshot([], [], [], []));
            finishDisposal.TrySetResult();
            if (failCleanup)
            {
                await Assert.ThrowsAsync<AggregateException>(
                    () => supervisor.StopAndDrainAsync().WaitAsync(Budget));
            }
            else
            {
                await supervisor.StopAndDrainAsync().WaitAsync(Budget);
            }
        }
    }

    [Fact]
    public async Task StopAndDrain_RepeatedCallsDisposeSessionOnlyOnce()
    {
        var fixture = await CreateAsync();
        var supervisor = fixture.Supervisor;
        var session = fixture.Sessions.Single();
        var disposalEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishDisposal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.OnDispose = async () =>
        {
            disposalEntered.SetResult();
            await finishDisposal.Task;
        };

        try
        {
            var first = supervisor.StopAndDrainAsync();
            await disposalEntered.Task.WaitAsync(Budget);
            var second = supervisor.StopAndDrainAsync();
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
            Assert.Equal(1, session.DisposeCount);

            finishDisposal.SetResult();
            await Task.WhenAll(first, second).WaitAsync(Budget);
            await supervisor.StopAndDrainAsync().WaitAsync(Budget);
            Assert.Equal(1, session.DisposeCount);
        }
        finally
        {
            finishDisposal.TrySetResult();
            await supervisor.StopAndDrainAsync().WaitAsync(Budget);
        }
    }

    [Fact]
    public async Task StopAndDrain_PropagatesSessionCleanupFailureWithoutRetryingDisposal()
    {
        var fixture = await CreateAsync();
        var supervisor = fixture.Supervisor;
        var session = fixture.Sessions.Single();
        var failure = new IOException("fixture cleanup failure");
        session.OnDispose = () => Task.FromException(failure);

        var first = await Assert.ThrowsAsync<AggregateException>(
            () => supervisor.StopAndDrainAsync().WaitAsync(Budget));
        var second = await Assert.ThrowsAsync<AggregateException>(
            () => supervisor.StopAndDrainAsync().WaitAsync(Budget));
        var disposal = await Assert.ThrowsAsync<AggregateException>(
            () => supervisor.DisposeAsync().AsTask().WaitAsync(Budget));

        Assert.Same(failure, Assert.Single(first.InnerExceptions));
        Assert.Same(first, second);
        Assert.Same(first, disposal);
        Assert.Equal(1, session.DisposeCount);
        Assert.False(supervisor.IsRunning);
    }

    [Fact]
    public async Task Dispose_WithoutDrainReportsDeferredCleanupFailureOnlyOnce()
    {
        var fixture = await CreateAsync();
        var supervisor = fixture.Supervisor;
        var session = fixture.Sessions.Single();
        var restartEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishRestart = new TaskCompletionSource<PluginHostSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var warningRecorded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new IOException("fixture unclaimed deferred cleanup failure");
        session.NextStart = _ =>
        {
            restartEntered.SetResult();
            return finishRestart.Task;
        };
        session.OnDispose = () => Task.FromException(failure);
        fixture.Logger.WarningWritten = (message, _) =>
        {
            if (message == "Deferred PluginHost supervisor cleanup faulted.")
                warningRecorded.TrySetResult();
        };

        try
        {
            session.Exit(1);
            await restartEntered.Task.WaitAsync(Budget);
            fixture.Logger.Warnings.Clear();

            var disposal = supervisor.DisposeAsync().AsTask();
            var timeout = await Assert.ThrowsAsync<TimeoutException>(
                () => disposal.WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.Contains("5 second budget", timeout.Message);
            Assert.Equal(0, session.DisposeCount);
            Assert.Empty(fixture.Logger.Warnings);

            finishRestart.SetResult(new PluginHostSnapshot([], [], [], []));
            await warningRecorded.Task.WaitAsync(Budget);

            var warning = Assert.Single(fixture.Logger.Warnings);
            var cleanupFailure = Assert.IsType<AggregateException>(warning.Exception);
            Assert.Same(failure, Assert.Single(cleanupFailure.InnerExceptions));
            Assert.Equal("Deferred PluginHost supervisor cleanup faulted.", warning.Message);
            Assert.Equal(1, session.DisposeCount);
            Assert.False(supervisor.IsRunning);
            Assert.Same(disposal, supervisor.DisposeAsync().AsTask());
        }
        finally
        {
            finishRestart.TrySetResult(new PluginHostSnapshot([], [], [], []));
            await warningRecorded.Task.WaitAsync(Budget);
        }
    }

    private static async Task ExhaustAsync(PluginHostSupervisor supervisor, FakeSession session)
    {
        await StatusAsync(supervisor, "Fixture 运行中", () => session.Exit(1));
        await StatusAsync(supervisor, "Fixture 连续异常退出，请手动重载", () => session.Exit(1));
        Assert.Equal(2, session.StartCount);
    }

    private static HashSet<string> RecoveryBudget(PluginHostSupervisor supervisor)
        => Assert.IsType<HashSet<string>>(typeof(PluginHostSupervisor)
            .GetField("_automaticRestartUsed", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(supervisor));

    private static async Task StatusAsync(PluginHostSupervisor supervisor, string expected, Action trigger)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(object? sender, EventArgs args)
        {
            if (supervisor.StatusText == expected) completion.TrySetResult();
        }
        supervisor.StatusChanged += Changed;
        try { trigger(); await completion.Task.WaitAsync(Budget); }
        finally { supervisor.StatusChanged -= Changed; }
        Assert.Equal(expected, supervisor.StatusText);
    }

    private async Task<(PluginHostSupervisor Supervisor, List<FakeSession> Sessions, TestLogger Logger)> CreateAsync(
        bool includeSecondSession = false)
    {
        Directory.CreateDirectory(_root);
        var manager = new PluginPackageManager(new PluginInstallationPaths(_root), new PluginPackageVerifier(new Version(1, 6, 0)));
        string[] pluginIds = includeSecondSession ? ["fixture.plugin", "fixture.second"] : ["fixture.plugin"];
        foreach (var pluginId in pluginIds)
        {
            var commandId = pluginId + ".open";
            var manifest = new PluginManifest
            {
                PluginId = pluginId, DisplayName = "Fixture", Version = "1.0.0", MinAppVersion = "1.0.0", EntryAssembly = "fixture.dll",
                ActivationEvents = [PluginHostProtocol.CommandActivationPrefix + commandId],
                Contributions = new PluginContributions { Commands = [new PluginCommandContribution { Id = commandId, Title = "Fixture command" }] },
            };
            byte[] bytes = [1, 2, 3]; // 仅清单验证数据；不加载或启动真实插件。
            manifest.Files.Add(new PluginPackageFile { Path = "fixture.dll", Sha256 = Convert.ToHexString(SHA256.HashData(bytes)) });
            var package = Path.Combine(_root, pluginId + ".animeido-plugin");
            using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
            {
                using (var stream = archive.CreateEntry("plugin.json").Open()) await JsonSerializer.SerializeAsync(stream, manifest);
                using var entry = archive.CreateEntry("fixture.dll").Open();
                await entry.WriteAsync(bytes);
            }
            await manager.InstallPackageAsync(package).WaitAsync(Budget);
        }
        var sessions = new List<FakeSession>();
        var logger = new TestLogger();
        var supervisor = new PluginHostSupervisor(manager, new PluginContributionRegistry(), new HostedAnimePlaybackLauncher(),
            new NoProgress(), new NoGateway(), logger, descriptor =>
            {
                var session = new FakeSession(descriptor);
                sessions.Add(session);
                return session;
            });
        await supervisor.StartAsync().WaitAsync(Budget);
        foreach (var pluginId in pluginIds)
            await supervisor.InvokeCommandAsync(pluginId, pluginId + ".open").WaitAsync(Budget);
        logger.Warnings.Clear(); // 后续断言只观察恢复阶段，不包含无宿主的发现提示。
        return (supervisor, sessions, logger);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

    private sealed class FakeSession(HostedPluginDescriptor descriptor) : IPluginHostSession
    {
        public string PluginId => descriptor.Manifest.PluginId;
        public string DisplayName => descriptor.Manifest.DisplayName;
        public bool IsRunning { get; private set; }
        public event EventHandler<PluginHostSessionExitedEventArgs>? Exited;
        public int StartCount { get; private set; }
        public int CommandCount { get; private set; }
        public int DisposeCount { get; private set; }
        public Func<CancellationToken, Task<PluginHostSnapshot>>? NextStart { get; set; }
        public Func<CancellationToken, Task<HostedActivePlaybackContext?>>? ActiveContextRequest { get; set; }
        public Func<string, CancellationToken, Task>? CommandRequest { get; set; }
        public Func<string, CancellationToken, Task>? SettingsRequest { get; set; }
        public Func<Task>? OnDispose { get; set; }
        public async Task<PluginHostSnapshot> StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartCount++;
            var snapshot = NextStart is { } start ? await start(cancellationToken) : new PluginHostSnapshot([], [], [], []);
            IsRunning = snapshot.Failures.Count == 0;
            return snapshot;
        }
        public void Exit(int code) { IsRunning = false; Exited?.Invoke(this, new PluginHostSessionExitedEventArgs(code)); }
        public Action CaptureExitCallback()
        {
            var callback = Exited;
            return () => callback?.Invoke(this, new PluginHostSessionExitedEventArgs(1));
        }
        public Task<bool> HasActiveUiAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task InvokeCommandAsync(string commandId, CancellationToken cancellationToken = default)
        {
            CommandCount++;
            return CommandRequest?.Invoke(commandId, cancellationToken) ?? Task.CompletedTask;
        }
        public Task OpenSettingsAsync(string settingsId, CancellationToken cancellationToken = default)
            => SettingsRequest?.Invoke(settingsId, cancellationToken) ?? Task.CompletedTask;
        public Task LaunchAnimePlaybackAsync(AnimePlaybackRequest request, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<HostedActivePlaybackContext?> GetActiveContextAsync(CancellationToken cancellationToken = default)
            => ActiveContextRequest?.Invoke(cancellationToken) ?? Task.FromResult<HostedActivePlaybackContext?>(null);
        public async ValueTask DisposeAsync() { DisposeCount++; IsRunning = false; if (OnDispose is { } dispose) await dispose(); }
    }

    private sealed class TestLogger : ILogger<PluginHostSupervisor>
    {
        public ConcurrentQueue<(LogLevel Level, string Message, Exception? Exception)> Warnings { get; } = new();
        public Action<string, Exception?>? WarningWritten { get; set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
            {
                var message = formatter(state, exception);
                Warnings.Enqueue((logLevel, message, exception));
                WarningWritten?.Invoke(message, exception);
            }
        }
    }
    private sealed class NoProgress : IAnimePlaybackProgressSink
    {
        public Task RecordAsync(AnimePlaybackProgress progress, CancellationToken ct = default) => throw new InvalidOperationException("Unexpected callback");
    }
    private sealed class NoGateway : IPersonalAnimeDataGateway
    {
        public Task<IReadOnlyList<PersonalAnimeSelectionItem>> QuerySelectionAsync(PersonalAnimeSelectionQuery query, CancellationToken ct = default) => throw new InvalidOperationException("Unexpected callback");
        public Task<PersonalAnimeContextSnapshot> BuildContextAsync(PersonalAnimeContextRequest request, CancellationToken ct = default) => throw new InvalidOperationException("Unexpected callback");
        public Task<PersonalAnimeChangeApplyResult> ApplyChangesAsync(PersonalAnimeChangeSet changeSet, CancellationToken ct = default) => throw new InvalidOperationException("Unexpected callback");
    }
}
