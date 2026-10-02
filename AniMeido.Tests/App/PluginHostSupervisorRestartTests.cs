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

public sealed class PluginHostSupervisorRestartTests : IDisposable
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"AniMeido-restart-{Guid.NewGuid():N}");

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
    public async Task RestartException_IsWarningAndConsumesBudgetWithoutEscapingEvent()
    {
        var fixture = await CreateAsync();
        await using var supervisor = fixture.Supervisor;
        var session = fixture.Sessions.Single();
        var failure = new IOException("fixture restart failure");
        session.NextStart = () => Task.FromException<PluginHostSnapshot>(failure);
        await StatusAsync(supervisor, "Fixture 自动恢复失败：fixture restart failure", () => session.Exit(1));
        var warning = Assert.Single(fixture.Logger.Warnings);
        Assert.Same(failure, warning.Exception);
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
        session.NextStart = () => Task.FromResult(new PluginHostSnapshot([], [], [],
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

    private async Task<(PluginHostSupervisor Supervisor, List<FakeSession> Sessions, TestLogger Logger)> CreateAsync()
    {
        Directory.CreateDirectory(_root);
        var manifest = new PluginManifest
        {
            PluginId = "fixture.plugin", DisplayName = "Fixture", Version = "1.0.0", MinAppVersion = "1.0.0", EntryAssembly = "fixture.dll",
            ActivationEvents = [PluginHostProtocol.CommandActivationPrefix + "fixture.plugin.open"],
            Contributions = new PluginContributions { Commands = [new PluginCommandContribution { Id = "fixture.plugin.open", Title = "Fixture command" }] },
        };
        byte[] bytes = [1, 2, 3]; // 仅清单验证数据；不加载或启动真实插件。
        manifest.Files.Add(new PluginPackageFile { Path = "fixture.dll", Sha256 = Convert.ToHexString(SHA256.HashData(bytes)) });
        var package = Path.Combine(_root, "fixture.animeido-plugin");
        using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            using (var stream = archive.CreateEntry("plugin.json").Open()) await JsonSerializer.SerializeAsync(stream, manifest);
            using var entry = archive.CreateEntry("fixture.dll").Open();
            await entry.WriteAsync(bytes);
        }
        var manager = new PluginPackageManager(new PluginInstallationPaths(_root), new PluginPackageVerifier(new Version(1, 6, 0)));
        await manager.InstallPackageAsync(package).WaitAsync(Budget);
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
        await supervisor.InvokeCommandAsync(manifest.PluginId, "fixture.plugin.open").WaitAsync(Budget);
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
        public Func<Task<PluginHostSnapshot>>? NextStart { get; set; }
        public Func<Task>? OnDispose { get; set; }
        public async Task<PluginHostSnapshot> StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartCount++;
            var snapshot = NextStart is { } start ? await start() : new PluginHostSnapshot([], [], [], []);
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
        public Task InvokeCommandAsync(string commandId, CancellationToken cancellationToken = default) { CommandCount++; return Task.CompletedTask; }
        public Task OpenSettingsAsync(string settingsId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LaunchAnimePlaybackAsync(AnimePlaybackRequest request, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<HostedActivePlaybackContext?> GetActiveContextAsync(CancellationToken cancellationToken = default) => Task.FromResult<HostedActivePlaybackContext?>(null);
        public async ValueTask DisposeAsync() { DisposeCount++; IsRunning = false; if (OnDispose is { } dispose) await dispose(); }
    }

    private sealed class TestLogger : ILogger<PluginHostSupervisor>
    {
        public ConcurrentQueue<(string Message, Exception? Exception)> Warnings { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning) Warnings.Enqueue((formatter(state, exception), exception));
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
