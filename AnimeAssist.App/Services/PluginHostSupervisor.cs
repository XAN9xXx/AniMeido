using AniMeido.Contracts.Playback;
using AniMeido.Contracts.PersonalAnime;
using AniMeido.PluginProtocol;
using Microsoft.Extensions.Logging;

namespace AniMeido.App.Services;

public sealed class PluginHostSupervisor : IAsyncDisposable
{
    private readonly PluginPackageManager _packageManager;
    private readonly PluginContributionRegistry _contributions;
    private readonly HostedAnimePlaybackLauncher _playbackLauncher;
    private readonly IAnimePlaybackProgressSink _playbackProgressSink;
    private readonly IPersonalAnimeDataGateway _personalAnimeDataGateway;
    private readonly ILogger<PluginHostSupervisor> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sessionsSync = new();
    private readonly Dictionary<string, HostedPluginDescriptor> _descriptors =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PluginHostSession> _sessions =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _automaticRestartUsed =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public PluginHostSupervisor(
        PluginPackageManager packageManager,
        PluginContributionRegistry contributions,
        HostedAnimePlaybackLauncher playbackLauncher,
        IAnimePlaybackProgressSink playbackProgressSink,
        IPersonalAnimeDataGateway personalAnimeDataGateway,
        ILogger<PluginHostSupervisor> logger)
    {
        _packageManager = packageManager;
        _contributions = contributions;
        _playbackLauncher = playbackLauncher;
        _playbackProgressSink = playbackProgressSink;
        _personalAnimeDataGateway = personalAnimeDataGateway;
        _logger = logger;
        _contributions.CommandInvoker = InvokeCommandAsync;
        _contributions.SettingsInvoker = OpenSettingsAsync;
        _playbackLauncher.Attach(this);
    }

    public event EventHandler? StatusChanged;

    public string StatusText { get; private set; } = "未启动";

    public bool IsRunning
    {
        get
        {
            lock (_sessionsSync)
            {
                return _sessions.Values.Any(session => session.IsRunning);
            }
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await DiscoverPluginsCoreAsync(cancellationToken);
        }
        catch
        {
            await StopSessionsCoreAsync(clearContributions: true);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> HasActivePluginUiAsync(
        CancellationToken cancellationToken = default)
    {
        var sessions = SnapshotSessions();
        foreach (var session in sessions)
        {
            if (await session.HasActiveUiAsync(cancellationToken))
            {
                return true;
            }
        }

        return false;
    }

    public async Task ReloadAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await StopSessionsCoreAsync(clearContributions: true);
            await DiscoverPluginsCoreAsync(cancellationToken);
            _packageManager.MarkReloadApplied();
            _automaticRestartUsed.Clear();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task InvokeCommandAsync(
        string pluginId,
        string commandId)
    {
        var session = await GetStartedSessionAsync(
            pluginId,
            CancellationToken.None);
        await session.InvokeCommandAsync(commandId);
    }

    public async Task OpenSettingsAsync(
        string pluginId,
        string settingsId)
    {
        var session = await GetStartedSessionAsync(
            pluginId,
            CancellationToken.None);
        await session.OpenSettingsAsync(settingsId);
    }

    public async Task LaunchAnimePlaybackAsync(
        AnimePlaybackRequest request,
        CancellationToken cancellationToken)
    {
        var pluginId = GetPlaybackPluginId();
        var session = await GetStartedSessionAsync(
            pluginId,
            cancellationToken);
        await session.LaunchAnimePlaybackAsync(
            request,
            cancellationToken);
    }

    public async Task<HostedActivePlaybackContext?>
        GetActivePlaybackContextAsync(
            CancellationToken cancellationToken = default)
    {
        foreach (var session in SnapshotSessions())
        {
            if (!session.IsRunning)
            {
                continue;
            }

            try
            {
                var context = await session.GetActiveContextAsync(
                    cancellationToken);
                if (context is not null)
                {
                    return context;
                }
            }
            catch (Exception ex) when (
                ex is IOException
                or ObjectDisposedException
                or JsonPipeRpcException)
            {
                _logger.LogDebug(
                    ex,
                    "PluginHost {PluginId} ended while reading playback "
                        + "context.",
                    session.PluginId);
            }
        }

        return null;
    }

    private async Task DiscoverPluginsCoreAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_descriptors.Count > 0)
        {
            return;
        }

        var directories = await _packageManager.PrepareForStartupAsync(
            cancellationToken);
        foreach (var directory in directories)
        {
            var manifest = PluginManifest.LoadFromFile(
                Path.Combine(directory, "plugin.json"))
                ?? throw new PluginOperationException(
                    "已验证插件缺少 plugin.json。");
            if (!_descriptors.TryAdd(
                manifest.PluginId,
                new HostedPluginDescriptor(directory, manifest)))
            {
                throw new PluginOperationException(
                    $"插件 ID 重复：{manifest.PluginId}");
            }
        }

        ApplyManifestContributions();
        if (_descriptors.Count == 0)
        {
            SetStatus("没有已启用的可选插件");
            return;
        }

        var hostPath = ResolveHostPath();
        if (!File.Exists(hostPath))
        {
            SetStatus("PluginHost 可执行文件不存在");
            _logger.LogError(
                "PluginHost executable was not found at {HostPath}.",
                hostPath);
            return;
        }

        SetStatus($"已发现 {_descriptors.Count} 个可选插件，按需启动");
        foreach (var descriptor in _descriptors.Values.Where(item =>
            item.Manifest.ActivationEvents.Contains(
                PluginHostProtocol.StartupFinishedActivationEvent,
                StringComparer.Ordinal)))
        {
            var session = GetOrCreateSession(descriptor);
            await StartSessionAsync(
                session,
                cancellationToken,
                resetRecoveryBudget: true);
        }
    }

    private async Task<PluginHostSession> GetStartedSessionAsync(
        string pluginId,
        CancellationToken cancellationToken)
    {
        await EnsureDiscoveredAsync(cancellationToken);
        PluginHostSession session;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_descriptors.TryGetValue(pluginId, out var descriptor))
            {
                throw new InvalidOperationException(
                    $"未找到已启用的插件：{pluginId}");
            }

            session = GetOrCreateSession(descriptor);
        }
        finally
        {
            _gate.Release();
        }

        await StartSessionAsync(
            session,
            cancellationToken,
            resetRecoveryBudget: true);
        return session;
    }

    private async Task EnsureDiscoveredAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_descriptors.Count == 0)
            {
                await DiscoverPluginsCoreAsync(cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private PluginHostSession GetOrCreateSession(
        HostedPluginDescriptor descriptor)
    {
        lock (_sessionsSync)
        {
            if (_sessions.TryGetValue(
                descriptor.Manifest.PluginId,
                out var existing))
            {
                return existing;
            }

            var session = new PluginHostSession(
                descriptor,
                ResolveHostPath(),
                _playbackProgressSink,
                _personalAnimeDataGateway,
                _logger);
            session.Exited += OnSessionExited;
            _sessions.Add(descriptor.Manifest.PluginId, session);
            return session;
        }
    }

    private async Task StartSessionAsync(
        PluginHostSession session,
        CancellationToken cancellationToken,
        bool resetRecoveryBudget)
    {
        var snapshot = await session.StartAsync(cancellationToken);
        if (snapshot.Failures.Count > 0)
        {
            await RecordFailuresAsync(snapshot.Failures, cancellationToken);
            var message = snapshot.Failures[0].Message;
            SetStatus($"{session.DisplayName} 加载失败：{message}");
            return;
        }

        if (resetRecoveryBudget)
        {
            _automaticRestartUsed.Remove(session.PluginId);
        }

        int runningCount;
        lock (_sessionsSync)
        {
            runningCount = _sessions.Values.Count(item => item.IsRunning);
        }
        SetStatus(
            $"{session.DisplayName} 运行中"
                + (runningCount > 1 ? $"（共 {runningCount} 个）" : string.Empty));
    }

    private async void OnSessionExited(
        object? sender,
        PluginHostSessionExitedEventArgs e)
    {
        if (sender is not PluginHostSession session || _disposed)
        {
            return;
        }

        if (PluginHostExitClassifier.IsNormal(e.ExitCode))
        {
            _automaticRestartUsed.Remove(session.PluginId);
            SetStatus($"{session.DisplayName} 已退出，将在需要时启动");
            return;
        }

        if (!_automaticRestartUsed.Add(session.PluginId))
        {
            SetStatus(
                $"{session.DisplayName} 连续异常退出，请手动重载");
            return;
        }

        SetStatus($"{session.DisplayName} 异常退出，正在自动恢复");
        try
        {
            await StartSessionAsync(
                session,
                CancellationToken.None,
                resetRecoveryBudget: false);
        }
#pragma warning disable CA1031 // A failed optional host restart must not crash the App.
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "PluginHost {PluginId} automatic restart failed.",
                session.PluginId);
            SetStatus(
                $"{session.DisplayName} 自动恢复失败：{ex.Message}");
        }
#pragma warning restore CA1031
    }

    private void ApplyManifestContributions()
    {
        var snapshots = _descriptors.Values
            .Select(descriptor =>
                PluginHostSession.CreateManifestSnapshot(
                    descriptor.Manifest))
            .ToList();
        _contributions.SetHostedContributions(
            snapshots.SelectMany(item => item.NavigationCommands).ToList(),
            snapshots.SelectMany(item => item.Settings).ToList());
        _playbackLauncher.SetAvailable(
            snapshots.SelectMany(item => item.Capabilities).Contains(
                PluginHostProtocol.AnimePlaybackCapability,
                StringComparer.Ordinal));
    }

    private string GetPlaybackPluginId()
        => _descriptors.Values.FirstOrDefault(item =>
            item.Manifest.Contributions.Capabilities.Contains(
                PluginHostProtocol.AnimePlaybackCapability,
                StringComparer.Ordinal))?.Manifest.PluginId
            ?? throw new InvalidOperationException(
                "当前没有可用的在线播放插件。");

    private async Task RecordFailuresAsync(
        IReadOnlyList<HostedPluginFailure> failures,
        CancellationToken cancellationToken)
    {
        await _packageManager.RecordLoadFailuresAsync(
            failures.ToDictionary(
                failure => failure.PluginId,
                failure => failure.Message,
                StringComparer.OrdinalIgnoreCase),
            cancellationToken);
        foreach (var failure in failures)
        {
            _logger.LogError(
                "Plugin {PluginId} failed in PluginHost: {Message}",
                failure.PluginId,
                failure.Message);
        }
    }

    private PluginHostSession[] SnapshotSessions()
    {
        lock (_sessionsSync)
        {
            return _sessions.Values.ToArray();
        }
    }

    private async Task StopSessionsCoreAsync(bool clearContributions)
    {
        PluginHostSession[] sessions;
        lock (_sessionsSync)
        {
            sessions = _sessions.Values.ToArray();
            _sessions.Clear();
        }
        foreach (var session in sessions)
        {
            session.Exited -= OnSessionExited;
            await session.DisposeAsync();
        }

        _descriptors.Clear();
        _automaticRestartUsed.Clear();
        if (clearContributions)
        {
            _contributions.SetHostedContributions([], []);
            _playbackLauncher.SetAvailable(false);
        }
    }

    private void SetStatus(string status)
    {
        StatusText = status;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string ResolveHostPath()
        => Path.Combine(
            AppContext.BaseDirectory,
            "PluginHost",
            "AniMeido.PluginHost.exe");

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _gate.WaitAsync();
        try
        {
            if (_disposed)
            {
                return;
            }

            await StopSessionsCoreAsync(clearContributions: true);
            _disposed = true;
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}

internal static class PluginHostExitClassifier
{
    public static bool IsNormal(int exitCode) => exitCode == 0;
}

internal static class PluginHostRpcTargetNames
{
    public const string HandshakeAsync = "HandshakeAsync";
    public const string InitializeAsync = "InitializeAsync";
    public const string InvokeCommandAsync = "InvokeCommandAsync";
    public const string OpenSettingsAsync = "OpenSettingsAsync";
    public const string LaunchAnimePlaybackAsync = "LaunchAnimePlaybackAsync";
    public const string GetRuntimeStateAsync = "GetRuntimeStateAsync";
    public const string GetPlaybackProgressEventsAsync =
        "GetPlaybackProgressEventsAsync";
    public const string AcknowledgePlaybackProgressEventsAsync =
        "AcknowledgePlaybackProgressEventsAsync";
    public const string GetActivePlaybackContextAsync =
        "GetActivePlaybackContextAsync";
    public const string ShutdownAsync = "ShutdownAsync";
}
