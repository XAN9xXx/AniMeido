using AniMeido.Contracts.Playback;
using AniMeido.Contracts.PersonalAnime;
using AniMeido.PluginProtocol;
using Microsoft.Extensions.Logging;

namespace AniMeido.App.Services;

public sealed class PluginHostSupervisor : IAsyncDisposable, IPluginReloadHost
{
    private static readonly TimeSpan GracefulStopTimeout =
        TimeSpan.FromSeconds(5);
    private readonly PluginPackageManager _packageManager;
    private readonly PluginContributionRegistry _contributions;
    private readonly HostedAnimePlaybackLauncher _playbackLauncher;
    private readonly Func<HostedPluginDescriptor, IPluginHostSession> _sessionFactory;
    private readonly ILogger<PluginHostSupervisor> _logger;
    private readonly object _lifecycleSync = new();
    // Keep the gate alive for late continuations; teardown must not race a
    // queued waiter releasing after the supervisor has been disposed.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sessionsSync = new();
    private readonly Dictionary<string, HostedPluginDescriptor> _descriptors =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IPluginHostSession> _sessions =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _automaticRestartUsed =
        new(StringComparer.OrdinalIgnoreCase);
    private int _disposed;
    private int _acceptingWork;
    private int _stopping;
    private int _stopGeneration;
    private CancellationTokenSource? _startupCancellation;
    private Task? _disposeTask;
    private Task? _cleanupTask;
    private bool _drainRequested;

    public PluginHostSupervisor(
        PluginPackageManager packageManager,
        PluginContributionRegistry contributions,
        HostedAnimePlaybackLauncher playbackLauncher,
        IAnimePlaybackProgressSink playbackProgressSink,
        IPersonalAnimeDataGateway personalAnimeDataGateway,
        ILogger<PluginHostSupervisor> logger)
        : this(packageManager, contributions, playbackLauncher, playbackProgressSink,
            personalAnimeDataGateway, logger, descriptor => new PluginHostSession(
                descriptor, ResolveHostPath(), playbackProgressSink, personalAnimeDataGateway, logger))
    {
    }

    internal PluginHostSupervisor(
        PluginPackageManager packageManager,
        PluginContributionRegistry contributions,
        HostedAnimePlaybackLauncher playbackLauncher,
        IAnimePlaybackProgressSink playbackProgressSink,
        IPersonalAnimeDataGateway personalAnimeDataGateway,
        ILogger<PluginHostSupervisor> logger,
        Func<HostedPluginDescriptor, IPluginHostSession> sessionFactory)
    {
        _packageManager = packageManager;
        _contributions = contributions;
        _playbackLauncher = playbackLauncher;
        _sessionFactory = sessionFactory;
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
            if (Volatile.Read(ref _disposed) != 0
                || Volatile.Read(ref _acceptingWork) == 0
                || Volatile.Read(ref _stopping) != 0)
            {
                return false;
            }

            lock (_sessionsSync)
            {
                return _sessions.Values.Any(session => session.IsRunning);
            }
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var observedStopGeneration = Volatile.Read(ref _stopGeneration);
        CancellationTokenSource? startupCancellation = null;
        var entered = false;
        var discoveryAttempted = false;
        lock (_lifecycleSync)
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposed) != 0,
                this);
            if (Volatile.Read(ref _stopping) != 0)
            {
                throw new InvalidOperationException("插件宿主管理器正在停止。");
            }
        }
        try
        {
            // Only the gate owner is an active startup. Queued callers wait
            // on their own token and must not overwrite this CTS.
            await _gate.WaitAsync(cancellationToken);
            entered = true;
            lock (_lifecycleSync)
            {
                ObjectDisposedException.ThrowIf(
                    Volatile.Read(ref _disposed) != 0,
                    this);
                if (Volatile.Read(ref _stopping) != 0
                    || observedStopGeneration != Volatile.Read(ref _stopGeneration))
                {
                    throw new InvalidOperationException("插件宿主管理器正在停止。");
                }

                startupCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken);
                _startupCancellation = startupCancellation;
            }
            var startupToken = startupCancellation!.Token;
            discoveryAttempted = true;
            await DiscoverPluginsCoreAsync(startupToken);
            Volatile.Write(ref _acceptingWork, 1);
        }
        catch
        {
            if (entered && discoveryAttempted)
            {
                await StopSessionsCoreAsync(clearContributions: true);
            }

            throw;
        }
        finally
        {
            if (entered)
            {
                _gate.Release();
            }

            if (startupCancellation is not null)
            {
                lock (_lifecycleSync)
                {
                    if (ReferenceEquals(_startupCancellation, startupCancellation))
                    {
                        _startupCancellation = null;
                    }
                }

                startupCancellation.Dispose();
            }
        }
    }

    public async Task<bool> HasActivePluginUiAsync(
        CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0
            || Volatile.Read(ref _acceptingWork) == 0
            || Volatile.Read(ref _stopping) != 0)
        {
            return false;
        }

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
        CancellationTokenSource? startupToCancel;
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        lock (_lifecycleSync)
        {
            if (Volatile.Read(ref _stopping) != 0)
            {
                throw new InvalidOperationException("插件宿主管理器正在停止。");
            }

            Volatile.Write(ref _stopping, 1);
            Volatile.Write(ref _acceptingWork, 0);
            startupToCancel = _startupCancellation;
            Interlocked.Increment(ref _stopGeneration);
        }
        CancelStartup(startupToCancel);
        var entered = false;
        CancellationTokenSource? reloadStartup = null;
        using var budget = CreateStopBudget(cancellationToken);
        try
        {
            await _gate.WaitAsync(budget.Token);
            entered = true;
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposed) != 0,
                this);
            await StopSessionsCoreAsync(clearContributions: true);
            lock (_lifecycleSync)
            {
                ObjectDisposedException.ThrowIf(
                    Volatile.Read(ref _disposed) != 0,
                    this);
                reloadStartup =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken);
                _startupCancellation = reloadStartup;
            }
            await DiscoverPluginsCoreAsync(reloadStartup!.Token);
            _packageManager.MarkReloadApplied();
            _automaticRestartUsed.Clear();
            Volatile.Write(ref _acceptingWork, 1);
        }
        finally
        {
            EndStartup(reloadStartup);
            if (entered)
            {
                _gate.Release();
            }
            Volatile.Write(ref _stopping, 0);
        }
    }

    public async Task InvokeCommandAsync(
        string pluginId,
        string commandId)
    {
        try
        {
            var session = await GetStartedSessionAsync(
                pluginId,
                CancellationToken.None);
            await session.InvokeCommandAsync(commandId);
        }
#pragma warning disable CA1031 // 调用边界记录本机最终失败，并保留原异常向调用方传播。
        catch (Exception ex) when (ex is not OperationCanceledException
            && ex is not JsonPipeRpcException { IsRemoteError: true })
        {
            _logger.LogError(ex, "PluginHost {PluginId} command {CommandId} failed.", pluginId, commandId);
            throw;
        }
#pragma warning restore CA1031
    }

    public async Task OpenSettingsAsync(
        string pluginId,
        string settingsId)
    {
        try
        {
            var session = await GetStartedSessionAsync(
                pluginId,
                CancellationToken.None);
            await session.OpenSettingsAsync(settingsId);
        }
#pragma warning disable CA1031 // 调用边界记录本机最终失败，并保留原异常向调用方传播。
        catch (Exception ex) when (ex is not OperationCanceledException
            && ex is not JsonPipeRpcException { IsRemoteError: true })
        {
            _logger.LogError(ex, "PluginHost {PluginId} settings {SettingsId} failed.", pluginId, settingsId);
            throw;
        }
#pragma warning restore CA1031
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
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        if (_descriptors.Count > 0)
        {
            return;
        }

        var prepared = await _packageManager.PrepareForStartupAsync(
            cancellationToken);
        foreach (var descriptor in prepared)
        {
            var manifest = descriptor.Manifest;
            if (!_descriptors.TryAdd(
                manifest.PluginId,
                descriptor))
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
            SetStatus("找不到插件运行程序，请重新安装 AniMeido");
            _logger.LogWarning(
                "PluginHost executable was not found at {HostPath}.",
                hostPath);
            return;
        }

        SetStatus($"已启用 {_descriptors.Count} 个可选插件，用到时自动启动");
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

    private async Task<IPluginHostSession> GetStartedSessionAsync(
        string pluginId,
        CancellationToken cancellationToken)
    {
        var observedStopGeneration = Volatile.Read(ref _stopGeneration);
        if (Volatile.Read(ref _stopping) != 0)
        {
            throw new InvalidOperationException("插件宿主管理器正在停止。");
        }

        await EnsureDiscoveredAsync(cancellationToken);
        if (Volatile.Read(ref _stopping) != 0
            || observedStopGeneration != Volatile.Read(ref _stopGeneration))
        {
            throw new InvalidOperationException("插件宿主管理器正在停止。");
        }
        IPluginHostSession session;
        await _gate.WaitAsync(cancellationToken);
        CancellationTokenSource? startupCancellation = null;
        try
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposed) != 0,
                this);
            if (Volatile.Read(ref _stopping) != 0
                || observedStopGeneration != Volatile.Read(ref _stopGeneration))
            {
                throw new InvalidOperationException("插件宿主管理器正在停止。");
            }

            startupCancellation = BeginStartup(
                cancellationToken,
                observedStopGeneration);
            if (!_descriptors.TryGetValue(pluginId, out var descriptor))
            {
                throw new InvalidOperationException(
                    $"未找到已启用的插件：{pluginId}");
            }

            session = GetOrCreateSession(descriptor);
            await StartSessionAsync(
                session,
                startupCancellation!.Token,
                resetRecoveryBudget: true);
        }
        finally
        {
            EndStartup(startupCancellation);
            _gate.Release();
        }
        return session;
    }

    private async Task EnsureDiscoveredAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        CancellationTokenSource? startupCancellation = null;
        try
        {
            var observedStopGeneration = Volatile.Read(ref _stopGeneration);
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposed) != 0,
                this);
            if (Volatile.Read(ref _stopping) != 0)
            {
                throw new InvalidOperationException("插件宿主管理器正在停止。");
            }
            startupCancellation = BeginStartup(
                cancellationToken,
                observedStopGeneration);
            if (_descriptors.Count == 0)
            {
                await DiscoverPluginsCoreAsync(startupCancellation!.Token);
            }
        }
        finally
        {
            EndStartup(startupCancellation);
            _gate.Release();
        }
    }

    private CancellationTokenSource BeginStartup(
        CancellationToken cancellationToken,
        int observedStopGeneration)
    {
        lock (_lifecycleSync)
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposed) != 0,
                this);
            if (Volatile.Read(ref _stopping) != 0
                || observedStopGeneration != Volatile.Read(ref _stopGeneration))
            {
                throw new InvalidOperationException("插件宿主管理器正在停止。");
            }

            var startupCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
            _startupCancellation = startupCancellation;
            return startupCancellation;
        }
    }

    private void EndStartup(CancellationTokenSource? startupCancellation)
    {
        if (startupCancellation is null)
        {
            return;
        }

        lock (_lifecycleSync)
        {
            if (ReferenceEquals(_startupCancellation, startupCancellation))
            {
                _startupCancellation = null;
            }
        }

        startupCancellation.Dispose();
    }

    private IPluginHostSession GetOrCreateSession(
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

            var session = _sessionFactory(descriptor);
            session.Exited += OnSessionExited;
            _sessions.Add(descriptor.Manifest.PluginId, session);
            return session;
        }
    }

    private async Task StartSessionAsync(
        IPluginHostSession session,
        CancellationToken cancellationToken,
        bool resetRecoveryBudget)
    {
        var snapshot = await session.StartAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
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
        if (sender is not IPluginHostSession session)
        {
            return;
        }

        var generation = Volatile.Read(ref _stopGeneration);
        var entered = false;
        CancellationTokenSource? startupCancellation = null;
        try
        {
            await _gate.WaitAsync();
            entered = true;
            if (Volatile.Read(ref _disposed) != 0
                || Volatile.Read(ref _acceptingWork) == 0
                || generation != Volatile.Read(ref _stopGeneration))
            {
                return;
            }

            lock (_sessionsSync)
            {
                if (!_sessions.TryGetValue(session.PluginId, out var current)
                    || !ReferenceEquals(current, session)
                    || session.IsRunning)
                {
                    return;
                }
            }

            if (PluginHostExitClassifier.IsNormal(e.ExitCode))
            {
                _automaticRestartUsed.Remove(session.PluginId);
                SetStatus($"{session.DisplayName} 已关闭，用到时会自动启动");
                return;
            }

            if (!_automaticRestartUsed.Add(session.PluginId))
            {
                SetStatus($"{session.DisplayName} 连续异常退出，请手动重载");
                return;
            }

            startupCancellation = BeginStartup(CancellationToken.None, generation);
            SetStatus($"{session.DisplayName} 异常退出，正在自动恢复");
            await StartSessionAsync(
                session,
                startupCancellation.Token,
                resetRecoveryBudget: false);
        }
        catch (OperationCanceledException) when (startupCancellation?.IsCancellationRequested == true)
        {
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0)
        {
        }
        catch (InvalidOperationException) when (generation != Volatile.Read(ref _stopGeneration))
        {
        }
#pragma warning disable CA1031 // 自动恢复是事件处理边界；失败保留诊断，不使应用退出。
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "PluginHost {PluginId} automatic restart failed.",
                session.PluginId);
            if (generation == Volatile.Read(ref _stopGeneration)
                && Volatile.Read(ref _acceptingWork) != 0)
            {
                SetStatus($"{session.DisplayName} 自动恢复失败：{ex.Message}");
            }
        }
#pragma warning restore CA1031
        finally
        {
            EndStartup(startupCancellation);
            if (entered)
            {
                _gate.Release();
            }
        }
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
            _logger.LogWarning(
                "Plugin {PluginId} failed in PluginHost: {Message}",
                failure.PluginId,
                failure.Message);
        }
    }

    private IPluginHostSession[] SnapshotSessions()
    {
        lock (_sessionsSync)
        {
            return _sessions.Values.ToArray();
        }
    }

    private async Task StopSessionsCoreAsync(bool clearContributions)
    {
        Volatile.Write(ref _acceptingWork, 0);
        IPluginHostSession[] sessions;
        lock (_sessionsSync)
        {
            sessions = _sessions.Values.ToArray();
            _sessions.Clear();
        }
        var failures = new List<Exception>();
        foreach (var session in sessions)
        {
            session.Exited -= OnSessionExited;
            try
            {
                await session.DisposeAsync();
            }
#pragma warning disable CA1031 // Teardown must attempt every session.
            catch (Exception ex)
            {
                failures.Add(ex);
            }
#pragma warning restore CA1031
        }

        _descriptors.Clear();
        _automaticRestartUsed.Clear();
        if (clearContributions)
        {
            _contributions.SetHostedContributions([], []);
            _playbackLauncher.SetAvailable(false);
        }

        if (failures.Count > 0)
        {
            throw new AggregateException(
                "One or more PluginHost sessions failed to dispose.",
                failures);
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

    private static CancellationTokenSource CreateStopBudget(
        CancellationToken cancellationToken)
    {
        var budget = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        budget.CancelAfter(GracefulStopTimeout);
        return budget;
    }

    internal Task StopAndDrainAsync()
        => BeginDisposal(claimDrain: true).Cleanup;

    public ValueTask DisposeAsync()
        => new(BeginDisposal(claimDrain: false).BoundedWait);

    private (Task Cleanup, Task BoundedWait) BeginDisposal(bool claimDrain)
    {
        TaskCompletionSource cleanup;
        TaskCompletionSource boundedWait;
        CancellationTokenSource? startupCancellation;
        lock (_lifecycleSync)
        {
            _drainRequested |= claimDrain;
            if (_cleanupTask is not null)
                return (_cleanupTask, _disposeTask!);

            Interlocked.Exchange(ref _disposed, 1);
            Volatile.Write(ref _acceptingWork, 0);
            Volatile.Write(ref _stopping, 1);
            Interlocked.Increment(ref _stopGeneration);
            startupCancellation = _startupCancellation;
            cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            boundedWait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // 取消回调可能同步重入两个入口，先公布同一次真实清理和公开等待。
            _cleanupTask = cleanup.Task;
            _disposeTask = boundedWait.Task;
        }

        CancelStartup(startupCancellation);
        var gateEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var budget = CreateStopBudget(CancellationToken.None);
        _ = CompleteCleanupAsync(cleanup, gateEntered);
        _ = CompleteDisposalWaitAsync(gateEntered.Task, cleanup.Task, boundedWait, budget);
        return (cleanup.Task, boundedWait.Task);
    }

    private async Task CompleteCleanupAsync(TaskCompletionSource completion, TaskCompletionSource gateEntered)
    {
        Exception? failure = null;
        try
        {
            await _gate.WaitAsync();
            try
            {
                gateEntered.TrySetResult();
                await StopSessionsCoreAsync(clearContributions: true);
            }
            finally
            {
                _gate.Release();
            }
        }
#pragma warning disable CA1031 // 真实清理只执行一次，原始失败交给共享任务的最终等待方。
        catch (Exception ex)
        {
            failure = ex;
            gateEntered.TrySetException(ex);
        }
#pragma warning restore CA1031
        finally
        {
            Volatile.Write(ref _stopping, 0);
        }

        if (failure is null)
            completion.TrySetResult();
        else
            completion.TrySetException(failure);
    }

    private async Task CompleteDisposalWaitAsync(
        Task gateEntered,
        Task cleanup,
        TaskCompletionSource completion,
        CancellationTokenSource budget)
    {
        using (budget)
        {
            try
            {
                try
                {
                    // 公开预算只限制取锁等待；已进入的会话清理仍须等到实际结束。
                    await gateEntered.WaitAsync(budget.Token);
                }
                catch (OperationCanceledException) when (budget.IsCancellationRequested)
                {
                    _ = ObserveUnclaimedCleanupAsync(cleanup);
                    completion.TrySetException(new TimeoutException(
                        "PluginHost supervisor disposal exceeded its 5 second budget."));
                    return;
                }

                await cleanup;
                if (budget.IsCancellationRequested)
                {
                    _logger.LogWarning(
                        "PluginHost supervisor exceeded the graceful "
                            + "disposal budget; forced cleanup completed.");
                }
                completion.TrySetResult();
            }
#pragma warning disable CA1031 // 公开等待保持原始清理结果，超时也保留真实清理任务。
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
#pragma warning restore CA1031
        }
    }

    private async Task ObserveUnclaimedCleanupAsync(Task cleanup)
    {
        try
        {
            await cleanup;
        }
#pragma warning disable CA1031 // 公开等待超时后，仅在应用尚未接管最终排空时报告一次。
        catch (Exception ex)
        {
            lock (_lifecycleSync)
            {
                if (!_drainRequested)
                {
                    _logger.LogWarning(ex, "Deferred PluginHost supervisor cleanup faulted.");
                }
            }
        }
#pragma warning restore CA1031
    }

    private void CancelStartup(
        CancellationTokenSource? startupCancellation)
    {
        if (startupCancellation is null)
        {
            return;
        }

        try
        {
            startupCancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The startup owner already completed; teardown still owns the
            // serialized gate cleanup and must continue.
        }
        catch (AggregateException ex)
        {
            _logger.LogWarning(
                ex,
                "PluginHost supervisor startup cancellation callback faulted; "
                    + "continuing teardown.");
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
