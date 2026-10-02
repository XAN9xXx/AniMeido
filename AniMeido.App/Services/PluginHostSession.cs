using AniMeido.Contracts.Playback;
using AniMeido.Contracts.PersonalAnime;
using AniMeido.PluginProtocol;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;

namespace AniMeido.App.Services;

internal sealed class PluginHostSession : IAsyncDisposable
{
    private static readonly TimeSpan GracefulStopTimeout =
        TimeSpan.FromSeconds(5);
    private readonly HostedPluginDescriptor _descriptor;
    private readonly string _hostPath;
    private readonly IAnimePlaybackProgressSink _playbackProgressSink;
    private readonly IPersonalAnimeDataGateway _personalAnimeDataGateway;
    private readonly ILogger _logger;
    private readonly object _lifecycleSync = new();
    // Keep the gate alive for late continuations; disposing it can race a queued
    // waiter completing and releasing after session teardown.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private JsonPipeRpcClient? _rpc;
    private NamedPipeServerStream? _pipe;
    private NamedPipeServerStream? _callbackPipe;
    private CancellationTokenSource? _callbackServerCancellation;
    private Task? _callbackServerTask;
    private CancellationTokenSource? _progressPumpCancellation;
    private Task? _progressPumpTask;
    private bool _intentionalStop;
    private int _acceptingWork;
    private int _disposed;
    private int _stopping;
    private int _stopGeneration;
    private CancellationTokenSource? _startupCancellation;
    private Task? _stopTask;
    private Task? _disposeTask;
    private Task? _deferredCleanupTask;

    public PluginHostSession(
        HostedPluginDescriptor descriptor,
        string hostPath,
        IAnimePlaybackProgressSink playbackProgressSink,
        IPersonalAnimeDataGateway personalAnimeDataGateway,
        ILogger logger)
    {
        _descriptor = descriptor;
        _hostPath = hostPath;
        _playbackProgressSink = playbackProgressSink;
        _personalAnimeDataGateway = personalAnimeDataGateway;
        _logger = logger;
    }

    public event EventHandler<PluginHostSessionExitedEventArgs>? Exited;

    public string PluginId => _descriptor.Manifest.PluginId;

    public string DisplayName => _descriptor.Manifest.DisplayName;

    public bool IsRunning =>
        Volatile.Read(ref _disposed) == 0
        && Volatile.Read(ref _acceptingWork) != 0
        && Volatile.Read(ref _stopping) == 0
        && _process is { HasExited: false }
        && _rpc is { IsUsable: true };

    public async Task<PluginHostSnapshot> StartAsync(
        CancellationToken cancellationToken = default)
    {
        var observedStopGeneration = Volatile.Read(ref _stopGeneration);
        CancellationTokenSource? startupCancellation = null;
        var entered = false;
        var startAttempted = false;
        lock (_lifecycleSync)
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposed) != 0,
                this);
            if (Volatile.Read(ref _stopping) != 0)
            {
                throw new InvalidOperationException(
                    $"插件宿主正在停止：{PluginId}");
            }
        }
        try
        {
            // Do not register a queued waiter as the active startup.  Stop
            // must cancel the CTS belonging to the owner that actually holds
            // the gate, not whichever caller most recently queued here.
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
                    throw new InvalidOperationException(
                        $"插件宿主正在停止：{PluginId}");
                }

                startupCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken);
                _startupCancellation = startupCancellation;
            }
            var startupToken = startupCancellation!.Token;
            if (IsRunning)
            {
                return CreateManifestSnapshot(_descriptor.Manifest);
            }

            if (_process is not null
                || _rpc is not null
                || _pipe is not null
                || _callbackPipe is not null)
            {
                await StopCoreAsync(cancellationToken);
            }

            startAttempted = true;
            return await StartCoreAsync(startupToken);
        }
        catch
        {
            if (startAttempted
                && (_process is not null
                || _rpc is not null
                || _pipe is not null
                || _callbackPipe is not null))
            {
                await StopCoreAsync(CancellationToken.None);
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

    public async Task<bool> HasActiveUiAsync(
        CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _acceptingWork) == 0 || !IsRunning)
        {
            return false;
        }

        var rpc = _rpc;
        if (rpc is null)
        {
            return false;
        }

        try
        {
            var state = await rpc.InvokeAsync<PluginHostRuntimeState>(
                PluginHostRpcTargetNames.GetRuntimeStateAsync,
                [],
                cancellationToken);
            return state.HasVisibleWindows || state.ActiveInvocationCount > 0;
        }
        catch (Exception ex) when (
            ex is IOException
            or ObjectDisposedException
            or OperationCanceledException
            or JsonPipeRpcException)
        {
            return false;
        }
    }

    public async Task InvokeCommandAsync(
        string commandId,
        CancellationToken cancellationToken = default)
    {
        var rpc = await GetRpcAsync(cancellationToken);
        await rpc.InvokeAsync(
            PluginHostRpcTargetNames.InvokeCommandAsync,
            [PluginId, commandId],
            cancellationToken);
    }

    public async Task OpenSettingsAsync(
        string settingsId,
        CancellationToken cancellationToken = default)
    {
        var rpc = await GetRpcAsync(cancellationToken);
        await rpc.InvokeAsync(
            PluginHostRpcTargetNames.OpenSettingsAsync,
            [PluginId, settingsId],
            cancellationToken);
    }

    public async Task LaunchAnimePlaybackAsync(
        AnimePlaybackRequest request,
        CancellationToken cancellationToken)
    {
        var rpc = await GetRpcAsync(cancellationToken);
        await rpc.InvokeAsync(
            PluginHostRpcTargetNames.LaunchAnimePlaybackAsync,
            [request],
            cancellationToken);
    }

    public async Task<HostedActivePlaybackContext?> GetActiveContextAsync(
        CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _acceptingWork) == 0 || !IsRunning)
        {
            return null;
        }

        var rpc = _rpc;
        if (rpc is null)
        {
            return null;
        }

        return await rpc.InvokeNullableAsync<HostedActivePlaybackContext>(
            PluginHostRpcTargetNames.GetActivePlaybackContextAsync,
            [],
            cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        TaskCompletionSource completion;
        CancellationTokenSource? startupCancellation;
        lock (_lifecycleSync)
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposed) != 0,
                this);
            if (Volatile.Read(ref _stopping) != 0)
            {
                return _stopTask
                    ?? _deferredCleanupTask
                    ?? Task.CompletedTask;
            }

            Volatile.Write(ref _stopping, 1);
            Volatile.Write(ref _acceptingWork, 0);
            Interlocked.Increment(ref _stopGeneration);
            startupCancellation = _startupCancellation;
            completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            // Publish the stable task before cancellation or task startup;
            // both can synchronously re-enter lifecycle callbacks.
            _stopTask = completion.Task;
        }

        CancelStartup(startupCancellation);
        _ = StopWithGateAsync(completion, cancellationToken);
        return completion.Task;
    }

    private async Task StopWithGateAsync(
        TaskCompletionSource completion,
        CancellationToken cancellationToken)
    {
        var budget = CreateStopBudget(cancellationToken);
        var entered = false;
        var budgetTransferred = false;
        Exception? failure = null;
        try
        {
            try
            {
                await _gate.WaitAsync(budget.Token);
            }
            catch (OperationCanceledException) when (budget.IsCancellationRequested)
            {
                // Do not wait without a bound on the caller's cleanup task.
                // The deferred continuation remains serialized behind the
                // current owner and keeps the session in stopping state.
                var deferred = StartDeferredStop(budget);
                TrackDeferredCleanup(deferred);
                CompleteWithBudgetFailure(completion, cancellationToken);
                budgetTransferred = true;
                return;
            }
            entered = true;
            await StopCoreWithBudgetAsync(budget.Token);
        }
#pragma warning disable CA1031 // Transfer the exception to the published completion.
        catch (Exception ex)
        {
            failure = ex;
        }
#pragma warning restore CA1031
        finally
        {
            if (entered)
            {
                _gate.Release();
            }

            var forcedCleanup = budget.IsCancellationRequested;
            if (!budgetTransferred)
            {
                budget.Dispose();
            }
            if (entered)
            {
                CompleteStopLifecycle();
                if (failure is null)
                {
                    if (forcedCleanup)
                    {
                        _logger.LogWarning(
                            "PluginHost {PluginId} exceeded the graceful "
                                + "stop budget; forced cleanup completed.",
                            PluginId);
                    }

                    completion.TrySetResult();
                }
                else
                {
                    completion.TrySetException(failure);
                }
            }
            else if (failure is not null)
            {
                CompleteStopLifecycle();
                completion.TrySetException(failure);
            }
        }
    }

    private Task StartDeferredStop(CancellationTokenSource budget)
    {
        var deferred = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lifecycleSync)
        {
            // Publish before starting the continuation; a released gate can
            // otherwise complete cleanup synchronously before it is tracked.
            _deferredCleanupTask = deferred.Task;
        }
        _ = ContinueStopAfterGateAsync(budget, deferred);
        return deferred.Task;
    }

    private async Task ContinueStopAfterGateAsync(
        CancellationTokenSource budget,
        TaskCompletionSource completion)
    {
        Exception? failure = null;
        try
        {
            await _gate.WaitAsync();
            try
            {
                await StopCoreWithBudgetAsync(budget.Token);
            }
            finally
            {
                _gate.Release();
            }
        }
#pragma warning disable CA1031 // Transfer the exception to the deferred completion.
        catch (Exception ex)
        {
            failure = ex;
        }
#pragma warning restore CA1031
        finally
        {
            budget.Dispose();
            CompleteStopLifecycle();
            if (failure is not null)
            {
                completion.TrySetException(failure);
            }
            else
            {
                completion.TrySetResult();
            }
        }
    }

    private void TrackDeferredCleanup(Task deferred)
    {
        _ = ObserveDeferredCleanupAsync(deferred);
    }

    private async Task ObserveDeferredCleanupAsync(Task deferred)
    {
        try
        {
            await deferred;
        }
#pragma warning disable CA1031 // Deferred cleanup has its own logging boundary.
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Deferred PluginHost {PluginId} cleanup faulted.",
                PluginId);
        }
#pragma warning restore CA1031
    }

    private void CompleteStopLifecycle()
    {
        lock (_lifecycleSync)
        {
            _deferredCleanupTask = null;
            _stopTask = null;
            Volatile.Write(ref _stopping, 0);
        }
    }

    private static void CompleteWithBudgetFailure(
        TaskCompletionSource completion,
        CancellationToken callerCancellation)
    {
        if (callerCancellation.IsCancellationRequested)
        {
            completion.TrySetCanceled(callerCancellation);
        }
        else
        {
            completion.TrySetException(
                new TimeoutException("PluginHost stop exceeded its 5 second budget."));
        }
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
            // The owner already completed and disposed its CTS; the stop
            // continuation still owns serialized gate cleanup.
        }
        catch (AggregateException ex)
        {
            _logger.LogWarning(
                ex,
                "PluginHost {PluginId} startup cancellation callback faulted; "
                    + "continuing teardown.",
                PluginId);
        }
    }

    private async Task<JsonPipeRpcClient> GetRpcAsync(
        CancellationToken cancellationToken)
    {
        var observedStopGeneration = Volatile.Read(ref _stopGeneration);
        await StartAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposed) != 0,
                this);
            if (Volatile.Read(ref _acceptingWork) == 0
                || Volatile.Read(ref _stopping) != 0
                || observedStopGeneration != Volatile.Read(ref _stopGeneration)
                || !IsRunning
                || _rpc is null)
            {
                throw new InvalidOperationException(
                    $"插件宿主未连接：{PluginId}");
            }

            return _rpc;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<PluginHostSnapshot> StartCoreAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_hostPath))
        {
            throw new FileNotFoundException(
                "PluginHost 可执行文件不存在。",
                _hostPath);
        }

        var pipeName =
            $"AniMeido.PluginHost.{Environment.ProcessId}.{Guid.NewGuid():N}";
        var callbackPipeName =
            $"AniMeido.PluginHost.Callback.{Environment.ProcessId}.{Guid.NewGuid():N}";
        _pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        _callbackPipe = new NamedPipeServerStream(
            callbackPipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        _process = Process.Start(new ProcessStartInfo
        {
            FileName = _hostPath,
            Arguments = $"--pipe \"{pipeName}\" --callback-pipe \"{callbackPipeName}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("无法启动 PluginHost。");
        _process.EnableRaisingEvents = true;
        _process.Exited += OnProcessExited;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await Task.WhenAll(
            _pipe.WaitForConnectionAsync(timeout.Token),
            _callbackPipe.WaitForConnectionAsync(timeout.Token));

        _callbackServerCancellation = new CancellationTokenSource();
        var callbackTarget = new PersonalAnimeCallbackRpcTarget(
            _personalAnimeDataGateway,
            _descriptor.Manifest);
        _callbackServerTask = new JsonPipeRpcServer(
            _callbackPipe,
            (request, token) => callbackTarget.DispatchAsync(request, token)).RunAsync(
                _callbackServerCancellation.Token);
        _ = ObserveBackgroundTaskAsync(
            _callbackServerTask,
            "callback server");

        _rpc = new JsonPipeRpcClient(_pipe);
        var appVersion = typeof(PluginHostSession).Assembly
            .GetName()
            .Version?
            .ToString(3) ?? "0.0.0";
        await _rpc.InvokeAsync<PluginHostHandshakeResponse>(
            PluginHostRpcTargetNames.HandshakeAsync,
            [new PluginHostHandshakeRequest(
                PluginHostProtocol.Version,
                appVersion,
                Guid.NewGuid().ToString("N"),
                callbackPipeName)],
            timeout.Token);
        var snapshot = await _rpc.InvokeAsync<PluginHostSnapshot>(
            PluginHostRpcTargetNames.InitializeAsync,
            [new[] { _descriptor }],
            timeout.Token);
        cancellationToken.ThrowIfCancellationRequested();
        StartProgressPump();
        Volatile.Write(ref _acceptingWork, 1);
        return snapshot;
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        using var budget = CreateStopBudget(cancellationToken);
        await StopCoreWithBudgetAsync(budget.Token);
    }

    private async Task StopCoreWithBudgetAsync(CancellationToken budget)
    {
        _intentionalStop = true;
        Volatile.Write(ref _acceptingWork, 0);
        try
        {
            CancelBackgroundOperations();
            await StopProgressPumpAsync(budget);
            await StopCallbackServerAsync(budget);
            if (_rpc is { IsUsable: true } rpc
                && !budget.IsCancellationRequested)
            {
                try
                {
                    await rpc.InvokeAsync(
                        PluginHostRpcTargetNames.ShutdownAsync,
                        [],
                        budget);
                }
                catch (Exception ex) when (
                    ex is IOException
                    or ObjectDisposedException
                    or JsonPipeRpcException
                    or OperationCanceledException)
                {
                    _logger.LogDebug(
                        ex,
                        "PluginHost {PluginId} ended before shutdown "
                            + "acknowledged.",
                        PluginId);
                }
            }

            if (_process is { HasExited: false } process)
            {
                try
                {
                    await process.WaitForExitAsync(budget);
                }
                catch (OperationCanceledException)
                {
                    if (!process.HasExited)
                    {
                        try
                        {
                            process.Kill(entireProcessTree: true);
                        }
                        catch (InvalidOperationException ex)
                        {
                            _logger.LogDebug(
                                ex,
                                "PluginHost {PluginId} exited before kill.",
                                PluginId);
                        }
                        catch (Win32Exception ex)
                        {
                            _logger.LogDebug(
                                ex,
                                "PluginHost {PluginId} kill raced process exit.",
                                PluginId);
                        }
                        try
                        {
                            using var killWait = new CancellationTokenSource(
                                TimeSpan.FromSeconds(1));
                            await process.WaitForExitAsync(killWait.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            _logger.LogWarning(
                                "PluginHost {PluginId} did not exit after kill.",
                                PluginId);
                        }
                    }
                }
            }
        }
        finally
        {
            CleanupConnection();
            _intentionalStop = false;
        }
    }

    private async void OnProcessExited(object? sender, EventArgs e)
    {
        try
        {
            await HandleProcessExitedAsync(sender as Process);
        }
#pragma warning disable CA1031 // Process exit events must not crash the App.
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to handle PluginHost {PluginId} exit.",
                PluginId);
        }
#pragma warning restore CA1031
    }

    private async Task HandleProcessExitedAsync(Process? exitedProcess)
    {
        using var budget = CreateStopBudget(CancellationToken.None);
        var entered = false;
        try
        {
            await _gate.WaitAsync(budget.Token);
            entered = true;
            if (_intentionalStop
                || Volatile.Read(ref _disposed) != 0
                || exitedProcess is null
                || !ReferenceEquals(exitedProcess, _process))
            {
                return;
            }

            var exitCode = exitedProcess.ExitCode;
            Volatile.Write(ref _acceptingWork, 0);
            CancelBackgroundOperations();
            await StopProgressPumpAsync(budget.Token);
            await StopCallbackServerAsync(budget.Token);
            CleanupConnection();
            Exited?.Invoke(
                this,
                new PluginHostSessionExitedEventArgs(exitCode));
        }
        finally
        {
            if (entered)
            {
                _gate.Release();
            }
        }
    }

    private void CleanupConnection()
    {
        Volatile.Write(ref _acceptingWork, 0);
        _progressPumpCancellation?.Cancel();
        if (_process is not null)
        {
            _process.Exited -= OnProcessExited;
            _process.Dispose();
            _process = null;
        }

        _rpc?.Dispose();
        _rpc = null;
        _pipe?.Dispose();
        _pipe = null;
        _callbackPipe?.Dispose();
        _callbackPipe = null;
    }

    private void CancelBackgroundOperations()
    {
        _progressPumpCancellation?.Cancel();
        _callbackServerCancellation?.Cancel();
    }

    private static CancellationTokenSource CreateStopBudget(
        CancellationToken cancellationToken)
    {
        var budget = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        budget.CancelAfter(GracefulStopTimeout);
        return budget;
    }

    private void StartProgressPump()
    {
        _progressPumpCancellation?.Cancel();
        _progressPumpCancellation?.Dispose();
        _progressPumpCancellation = new CancellationTokenSource();
        _progressPumpTask = PumpPlaybackProgressAsync(
            _progressPumpCancellation.Token);
        _ = ObserveBackgroundTaskAsync(
            _progressPumpTask,
            "playback progress");
    }

    private async Task ObserveBackgroundTaskAsync(
        Task task,
        string operation)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug(
                "PluginHost {PluginId} {Operation} stopped.",
                PluginId,
                operation);
        }
#pragma warning disable CA1031 // Background task faults are logged and do not crash the App.
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "PluginHost {PluginId} {Operation} faulted.",
                PluginId,
                operation);
        }
#pragma warning restore CA1031
    }

    private async Task StopCallbackServerAsync(CancellationToken budget)
    {
        var cancellation = _callbackServerCancellation;
        var task = _callbackServerTask;
        _callbackServerCancellation = null;
        _callbackServerTask = null;
        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        if (task is null)
        {
            cancellation.Dispose();
            return;
        }

        try
        {
            var result = await PluginHostTaskTeardown.CancelAndAwaitAsync(
                cancellation,
                task,
                budget,
                ex => _logger.LogDebug(
                    ex,
                    "PluginHost {PluginId} callback server ended during stop.",
                    PluginId));
            _ = result.DeferredCleanup;
        }
        catch (OperationCanceledException)
        {
            cancellation.Dispose();
        }
    }

    private async Task StopProgressPumpAsync(CancellationToken budget)
    {
        var cancellation = _progressPumpCancellation;
        var task = _progressPumpTask;
        _progressPumpCancellation = null;
        _progressPumpTask = null;
        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        if (task is null)
        {
            cancellation.Dispose();
            return;
        }

        try
        {
            var result = await PluginHostTaskTeardown.CancelAndAwaitAsync(
                cancellation,
                task,
                budget,
                ex => _logger.LogDebug(
                    ex,
                    "PluginHost {PluginId} progress pump ended during stop.",
                    PluginId));
            _ = result.DeferredCleanup;
        }
        catch (OperationCanceledException)
        {
            cancellation.Dispose();
        }
    }

    private async Task PumpPlaybackProgressAsync(
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var rpc = _rpc;
            if (rpc is null)
            {
                return;
            }

            try
            {
                var events = await rpc.InvokeAsync<
                    HostedPlaybackProgressEvent[]>(
                    PluginHostRpcTargetNames.GetPlaybackProgressEventsAsync,
                    [],
                    cancellationToken);
                long acknowledgedSequence = 0;
                foreach (var item in events.OrderBy(item => item.Sequence))
                {
                    try
                    {
                        await _playbackProgressSink.RecordAsync(
                            new AnimePlaybackProgress(
                                item.EventId,
                                item.AnimeId,
                                item.EpisodeNumber,
                                item.PositionSeconds,
                                item.DurationSeconds,
                                item.ReachedNaturalEnd,
                                item.ObservedAt),
                            cancellationToken);
                    }
                    catch (ArgumentException ex)
                    {
                        _logger.LogWarning(
                            ex,
                            "Discarding invalid playback progress event "
                                + "{EventId} at sequence {Sequence}.",
                            item.EventId,
                            item.Sequence);
                    }

                    acknowledgedSequence = item.Sequence;
                }

                if (acknowledgedSequence > 0)
                {
                    await rpc.InvokeAsync(
                        PluginHostRpcTargetNames
                            .AcknowledgePlaybackProgressEventsAsync,
                        [acknowledgedSequence],
                        cancellationToken);
                }

                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (
                ex is IOException
                or ObjectDisposedException
                or JsonPipeRpcException)
            {
                _logger.LogDebug(
                    ex,
                    "PluginHost {PluginId} playback progress channel ended.",
                    PluginId);
                return;
            }
            catch (Exception ex) when (
                ex is SqliteException
                or InvalidOperationException)
            {
                _logger.LogWarning(
                    ex,
                    "Playback progress persistence failed for {PluginId}; "
                        + "the unacknowledged batch will be retried.",
                    PluginId);
                await Task.Delay(
                    TimeSpan.FromSeconds(2),
                    cancellationToken);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        CancellationTokenSource? startupCancellation;
        lock (_lifecycleSync)
        {
            if (_disposeTask is not null)
            {
                return new ValueTask(_disposeTask);
            }

            Interlocked.Exchange(ref _disposed, 1);
            Volatile.Write(ref _acceptingWork, 0);
            Volatile.Write(ref _stopping, 1);
            Interlocked.Increment(ref _stopGeneration);
            startupCancellation = _startupCancellation;
            completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            // Publish before Cancel so a re-entrant Dispose observes the same
            // completion rather than starting a second teardown.
            _disposeTask = completion.Task;
        }

        CancelStartup(startupCancellation);
        _ = DisposeWithGateAsync(completion);
        return new ValueTask(completion.Task);
    }

    private async Task DisposeWithGateAsync(TaskCompletionSource completion)
    {
        var budget = CreateStopBudget(CancellationToken.None);
        var entered = false;
        var budgetTransferred = false;
        Exception? failure = null;
        try
        {
            try
            {
                await _gate.WaitAsync(budget.Token);
            }
            catch (OperationCanceledException) when (budget.IsCancellationRequested)
            {
                var deferred = StartDeferredDispose(budget);
                TrackDeferredCleanup(deferred);
                completion.TrySetException(
                    new TimeoutException("PluginHost disposal exceeded its 5 second budget."));
                budgetTransferred = true;
                return;
            }
            entered = true;
            await StopCoreWithBudgetAsync(budget.Token);
        }
#pragma warning disable CA1031 // Transfer the exception to the published completion.
        catch (Exception ex)
        {
            failure = ex;
        }
#pragma warning restore CA1031
        finally
        {
            if (entered)
            {
                _gate.Release();
            }

            if (!entered && !budgetTransferred)
            {
                budget.Dispose();
            }

            if (entered)
            {
                var forcedCleanup = budget.IsCancellationRequested;
                if (!budgetTransferred)
                {
                    budget.Dispose();
                }
                Volatile.Write(ref _stopping, 0);
                if (failure is null)
                {
                    if (forcedCleanup)
                    {
                        _logger.LogWarning(
                            "PluginHost {PluginId} exceeded the graceful "
                                + "disposal budget; forced cleanup completed.",
                            PluginId);
                    }

                    completion.TrySetResult();
                }
                else
                {
                    completion.TrySetException(failure);
                }
            }
            else if (failure is not null)
            {
                Volatile.Write(ref _stopping, 0);
                completion.TrySetException(failure);
            }
        }
    }

    private Task StartDeferredDispose(CancellationTokenSource budget)
    {
        var deferred = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lifecycleSync)
        {
            _deferredCleanupTask = deferred.Task;
        }
        _ = ContinueDisposeAfterGateAsync(budget, deferred);
        return deferred.Task;
    }

    private async Task ContinueDisposeAfterGateAsync(
        CancellationTokenSource budget,
        TaskCompletionSource completion)
    {
        Exception? failure = null;
        try
        {
            await _gate.WaitAsync();
            try
            {
                await StopCoreWithBudgetAsync(budget.Token);
            }
            finally
            {
                _gate.Release();
            }
        }
#pragma warning disable CA1031 // Transfer the exception to the deferred completion.
        catch (Exception ex)
        {
            failure = ex;
        }
#pragma warning restore CA1031
        finally
        {
            budget.Dispose();
            Volatile.Write(ref _stopping, 0);
            if (failure is not null)
            {
                completion.TrySetException(failure);
            }
            else
            {
                completion.TrySetResult();
            }
        }
    }

    internal static PluginHostSnapshot CreateManifestSnapshot(
        PluginManifest manifest)
    {
        var commands = new List<HostedCommandContribution>();
        foreach (var navigation in manifest.Contributions.Navigation)
        {
            var command = manifest.Contributions.Commands.Single(item =>
                string.Equals(
                    item.Id,
                    navigation.Command,
                    StringComparison.Ordinal));
            commands.Add(new HostedCommandContribution(
                manifest.PluginId,
                command.Id,
                command.Title,
                command.Icon));
        }

        var settings = manifest.Contributions.Settings.Select(item =>
            new HostedSettingsContribution(
                manifest.PluginId,
                manifest.DisplayName,
                item.Id,
                item.Title,
                item.Icon)).ToList();
        return new PluginHostSnapshot(
            commands,
            settings,
            manifest.Contributions.Capabilities.ToList(),
            []);
    }
}

internal readonly record struct PluginHostTaskTeardownResult(
    bool Completed,
    Task DeferredCleanup);

internal static class PluginHostTaskTeardown
{
    public static async Task<PluginHostTaskTeardownResult>
        CancelAndAwaitAsync(
            CancellationTokenSource cancellation,
            Task task,
            CancellationToken budget,
            Action<Exception>? observeException = null)
    {
        cancellation.Cancel();
        try
        {
            await task.WaitAsync(budget);
            cancellation.Dispose();
            return new(true, Task.CompletedTask);
        }
        catch (OperationCanceledException) when (!budget.IsCancellationRequested)
        {
            cancellation.Dispose();
            return new(true, Task.CompletedTask);
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested)
        {
            return new(
                false,
                DisposeAfterCompletionAsync(
                    cancellation,
                    task,
                    observeException));
        }
#pragma warning disable CA1031 // Teardown observes and defers arbitrary task faults.
        catch (Exception ex)
        {
            observeException?.Invoke(ex);
            cancellation.Dispose();
            return new(true, Task.CompletedTask);
        }
#pragma warning restore CA1031
    }

    private static async Task DisposeAfterCompletionAsync(
        CancellationTokenSource cancellation,
        Task task,
        Action<Exception>? observeException)
    {
        try
        {
            await task;
        }
#pragma warning disable CA1031 // Deferred teardown only observes task faults.
        catch (Exception ex)
        {
            observeException?.Invoke(ex);
        }
#pragma warning restore CA1031
        finally
        {
            cancellation.Dispose();
        }
    }
}

internal sealed class PluginHostSessionExitedEventArgs(int exitCode)
    : EventArgs
{
    public int ExitCode { get; } = exitCode;
}
