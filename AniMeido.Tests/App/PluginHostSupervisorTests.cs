using System.Diagnostics;
using System.Reflection;
using AniMeido.App.Services;
using AniMeido.Contracts.Playback;
using AniMeido.Contracts.PersonalAnime;
using AniMeido.PluginProtocol;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace AniMeido.Tests;

public sealed class PluginHostSupervisorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"AniMeido-supervisor-{Guid.NewGuid():N}");
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Start_EmptyInstallationPublishesStatusAndDoesNotStartProcess()
    {
        var (supervisor, registry, launcher) = Create();
        await using (supervisor)
        {
            Assert.Equal("未启动", supervisor.StatusText);
            var statuses = new List<string>();
            supervisor.StatusChanged += (_, _) => statuses.Add(supervisor.StatusText);
            await supervisor.StartAsync().WaitAsync(Budget);
            Assert.Equal("没有已启用的可选插件", supervisor.StatusText);
            Assert.Equal(new[] { "没有已启用的可选插件" }, statuses);
            Assert.False(supervisor.IsRunning);
            Assert.False(await supervisor.HasActivePluginUiAsync().WaitAsync(Budget));
            Assert.Null(await supervisor.GetActivePlaybackContextAsync().WaitAsync(Budget));
            Assert.False(launcher.IsAvailable);
            Assert.Empty(registry.NavigationItems);
            Assert.Empty(registry.Settings);
        }
    }

    [Fact]
    public async Task Start_PreCancelledThenRetryWorksAndReloadKeepsEmptyState()
    {
        var (supervisor, registry, launcher) = Create();
        await using (supervisor)
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => supervisor.StartAsync(cancellation.Token).WaitAsync(Budget));
            Assert.Equal("未启动", supervisor.StatusText);
            await supervisor.StartAsync().WaitAsync(Budget);
            await supervisor.ReloadAsync().WaitAsync(Budget);
            Assert.Equal("没有已启用的可选插件", supervisor.StatusText);
            Assert.False(supervisor.IsRunning);
            Assert.False(launcher.IsAvailable);
            Assert.Empty(registry.NavigationItems);
        }
    }

    [Fact]
    public async Task CommandsAndPlaybackWithoutPluginFailClearlyWithoutGatewayOrProcess()
    {
        var (supervisor, _, _) = Create();
        await using (supervisor)
        {
            var command = await Assert.ThrowsAsync<InvalidOperationException>(() => supervisor.InvokeCommandAsync("missing", "open").WaitAsync(Budget));
            Assert.Equal("未找到已启用的插件：missing", command.Message);
            var settings = await Assert.ThrowsAsync<InvalidOperationException>(() => supervisor.OpenSettingsAsync("missing", "settings").WaitAsync(Budget));
            Assert.Equal("未找到已启用的插件：missing", settings.Message);
            var playback = await Assert.ThrowsAsync<InvalidOperationException>(() => supervisor.LaunchAnimePlaybackAsync(
                new AnimePlaybackRequest(1, "fixture", []), CancellationToken.None).WaitAsync(Budget));
            Assert.Equal("当前没有可用的在线播放插件。", playback.Message);
            Assert.False(supervisor.IsRunning);
        }
    }

    [Fact]
    public async Task Dispose_IsSharedIdempotentAndRejectsFurtherStartup()
    {
        var (supervisor, _, _) = Create();
        await supervisor.StartAsync().WaitAsync(Budget);
        var first = supervisor.DisposeAsync().AsTask();
        var second = supervisor.DisposeAsync().AsTask();
        Assert.Same(first, second);
        await first.WaitAsync(Budget);
        Assert.False(supervisor.IsRunning);
        Assert.False(await supervisor.HasActivePluginUiAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => supervisor.StartAsync().WaitAsync(Budget));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => supervisor.ReloadAsync().WaitAsync(Budget));
    }

    [Fact]
    public async Task MissingHost_PreservesManifestContributionsButActivationThrowsFileNotFound()
    {
        // 当前测试输出不部署 WinUI 宿主；先断言该前提，避免将来部署变化时误启动真实应用。
        var expectedHost = Path.Combine(AppContext.BaseDirectory, "PluginHost", "AniMeido.PluginHost.exe");
        Assert.False(File.Exists(expectedHost), "此测试只能在没有真实 PluginHost 的测试输出中运行。");
        var manager = Manager();
        var manifest = new PluginManifest
        {
            PluginId = "fixture.plugin", DisplayName = "Fixture", Version = "1.0.0", MinAppVersion = "1.0.0", EntryAssembly = "fixture.dll",
            ActivationEvents = [PluginHostProtocol.CommandActivationPrefix + "fixture.plugin.open", PluginHostProtocol.AnimePlaybackActivationEvent],
            Contributions = new PluginContributions
            {
                Commands = [new PluginCommandContribution { Id = "fixture.plugin.open", Title = "Fixture command", Icon = "x" }],
                Navigation = [new PluginNavigationContribution { Command = "fixture.plugin.open" }],
                Capabilities = [PluginHostProtocol.AnimePlaybackCapability],
            },
        };
        var entryBytes = new byte[] { 1, 2, 3 }; // 包验证夹具，不是可执行的测试插件。
        manifest.Files.Add(new PluginPackageFile { Path = "fixture.dll", Sha256 = Convert.ToHexString(SHA256.HashData(entryBytes)) });
        Directory.CreateDirectory(_root);
        var package = Path.Combine(_root, "fixture.animeido-plugin");
        using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            using (var stream = archive.CreateEntry("plugin.json").Open())
                await JsonSerializer.SerializeAsync(stream, manifest);
            using var entry = archive.CreateEntry("fixture.dll").Open();
            await entry.WriteAsync(entryBytes);
        }
        await manager.InstallPackageAsync(package);
        var (supervisor, registry, launcher) = Create(manager);
        await using (supervisor)
        {
            await supervisor.StartAsync().WaitAsync(Budget);
            Assert.Equal("找不到插件运行程序，请重新安装 AniMeido", supervisor.StatusText);
            Assert.Equal("Fixture command", Assert.Single(registry.NavigationItems).Label);
            Assert.True(launcher.IsAvailable); // 当前为声明能力可用，并非宿主已经运行。
            Assert.False(supervisor.IsRunning);
            var error = await Assert.ThrowsAsync<FileNotFoundException>(() => supervisor.InvokeCommandAsync("fixture.plugin", "fixture.plugin.open").WaitAsync(Budget));
            Assert.Equal(expectedHost, error.FileName);
            Assert.False(supervisor.IsRunning);
            Assert.True(Assert.Single(await manager.GetInstalledPluginsAsync()).Enabled);
        }
        Assert.Empty(registry.NavigationItems);
        Assert.False(launcher.IsAvailable);
    }

    [Fact]
    public async Task Session_ProcessExitBeforeConnectionFailsPromptlyAndRetryCleansResourcesWithoutExited()
    {
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "where.exe");
        Assert.True(File.Exists(executable));
        var logger = new SessionLogger();
        await using var session = new PluginHostSession(
            new HostedPluginDescriptor(_root, new PluginManifest { PluginId = "fixture.process", DisplayName = "Fixture" }),
            executable, new NoProgress(), new NoGateway(), logger);
        var exited = 0;
        session.Exited += (_, _) => Interlocked.Increment(ref exited);
        string? firstMessage = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var cancellation = new CancellationTokenSource(Budget);
            var stopwatch = Stopwatch.StartNew();
            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.StartAsync(cancellation.Token).WaitAsync(Budget));
            Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, Budget);
            var match = System.Text.RegularExpressions.Regex.Match(
                error.Message, "^插件运行程序启动后立即退出（退出码 (-?\\d+)）。$");
            Assert.True(match.Success, error.Message);
            Assert.NotEqual(0, int.Parse(match.Groups[1].Value));
            if (firstMessage is not null) Assert.Equal(firstMessage, error.Message);
            firstMessage = error.Message;
            Assert.False(session.IsRunning);
            Assert.Null(GetSessionResource(session, "_process"));
            Assert.Null(GetSessionResource(session, "_pipe"));
            Assert.Null(GetSessionResource(session, "_callbackPipe"));
            Assert.Null(GetSessionResource(session, "_rpc"));
            Assert.Null(GetSessionResource(session, "_startupCancellation"));
            // 排在启动后的退出回调也必须完成，且不能发出 Exited 或预算警告。
            var gate = Assert.IsType<SemaphoreSlim>(GetSessionResource(session, "_gate"));
            await gate.WaitAsync().WaitAsync(Budget);
            gate.Release();
            Assert.Equal(0, Volatile.Read(ref exited));
            Assert.Empty(logger.Warnings);
        }
    }

    private sealed class SessionLogger : Microsoft.Extensions.Logging.ILogger
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Warnings { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel level, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (level >= Microsoft.Extensions.Logging.LogLevel.Warning) Warnings.Enqueue(formatter(state, exception));
        }
    }

    private static object? GetSessionResource(PluginHostSession session, string name)
        => typeof(PluginHostSession).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session);
    private PluginPackageManager Manager() => new(new PluginInstallationPaths(_root), new PluginPackageVerifier(new Version(1, 6, 0)));
    private (PluginHostSupervisor Supervisor, PluginContributionRegistry Registry, HostedAnimePlaybackLauncher Launcher) Create(PluginPackageManager? manager = null)
    {
        var registry = new PluginContributionRegistry();
        var launcher = new HostedAnimePlaybackLauncher();
        return (new PluginHostSupervisor(manager ?? Manager(), registry, launcher, new NoProgress(), new NoGateway(), NullLogger<PluginHostSupervisor>.Instance), registry, launcher);
    }
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
    private sealed class NoProgress : IAnimePlaybackProgressSink
    {
        public Task RecordAsync(AnimePlaybackProgress progress, CancellationToken ct = default) => throw new InvalidOperationException("Unexpected process callback");
    }
    private sealed class NoGateway : IPersonalAnimeDataGateway
    {
        public Task<IReadOnlyList<PersonalAnimeSelectionItem>> QuerySelectionAsync(PersonalAnimeSelectionQuery query, CancellationToken ct = default) => throw new InvalidOperationException("Unexpected process callback");
        public Task<PersonalAnimeContextSnapshot> BuildContextAsync(PersonalAnimeContextRequest request, CancellationToken ct = default) => throw new InvalidOperationException("Unexpected process callback");
        public Task<PersonalAnimeChangeApplyResult> ApplyChangesAsync(PersonalAnimeChangeSet changeSet, CancellationToken ct = default) => throw new InvalidOperationException("Unexpected process callback");
    }
}
