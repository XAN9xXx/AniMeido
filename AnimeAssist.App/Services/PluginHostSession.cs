using AniMeido.Contracts.Playback;
using AniMeido.PluginProtocol;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.IO.Pipes;

namespace AniMeido.App.Services;

internal sealed class PluginHostSession : IAsyncDisposable
{
    private readonly HostedPluginDescriptor _descriptor;
    private readonly string _hostPath;
    private readonly IAnimePlaybackProgressSink _playbackProgressSink;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private JsonPipeRpcClient? _rpc;
    private NamedPipeServerStream? _pipe;
    private CancellationTokenSource? _progressPumpCancellation;
    private Task? _progressPumpTask;
    private bool _intentionalStop;
    private bool _disposed;

    public PluginHostSession(
        HostedPluginDescriptor descriptor,
        string hostPath,
        IAnimePlaybackProgressSink playbackProgressSink,
        ILogger logger)
    {
        _descriptor = descriptor;
        _hostPath = hostPath;
        _playbackProgressSink = playbackProgressSink;
        _logger = logger;
    }

    public event EventHandler<PluginHostSessionExitedEventArgs>? Exited;

    public string PluginId => _descriptor.Manifest.PluginId;

    public string DisplayName => _descriptor.Manifest.DisplayName;

    public bool IsRunning =>
        _process is { HasExited: false } && _rpc is not null;

    public async Task<PluginHostSnapshot> StartAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsRunning)
            {
                return CreateManifestSnapshot(_descriptor.Manifest);
            }

            if (_process is not null || _rpc is not null || _pipe is not null)
            {
                await StopProgressPumpAsync();
                CleanupConnection();
            }

            return await StartCoreAsync(cancellationToken);
        }
        catch
        {
            await StopCoreAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> HasActiveUiAsync(
        CancellationToken cancellationToken = default)
    {
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
        var rpc = _rpc;
        if (rpc is null)
        {
            return null;
        }

        return await rpc.InvokeAsync<HostedActivePlaybackContext?>(
            PluginHostRpcTargetNames.GetActivePlaybackContextAsync,
            [],
            cancellationToken);
    }

    public async Task StopAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await StopCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<JsonPipeRpcClient> GetRpcAsync(
        CancellationToken cancellationToken)
    {
        await StartAsync(cancellationToken);
        return _rpc
            ?? throw new InvalidOperationException(
                $"插件宿主未连接：{PluginId}");
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
        _pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        _process = Process.Start(new ProcessStartInfo
        {
            FileName = _hostPath,
            Arguments = $"--pipe \"{pipeName}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("无法启动 PluginHost。");
        _process.EnableRaisingEvents = true;
        _process.Exited += OnProcessExited;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await _pipe.WaitForConnectionAsync(timeout.Token);

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
                Guid.NewGuid().ToString("N"))],
            timeout.Token);
        var snapshot = await _rpc.InvokeAsync<PluginHostSnapshot>(
            PluginHostRpcTargetNames.InitializeAsync,
            [new[] { _descriptor }],
            timeout.Token);
        StartProgressPump();
        return snapshot;
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        _intentionalStop = true;
        try
        {
            await StopProgressPumpAsync();
            if (_rpc is not null)
            {
                try
                {
                    await _rpc.InvokeAsync(
                        PluginHostRpcTargetNames.ShutdownAsync,
                        [],
                        cancellationToken);
                }
                catch (Exception ex) when (
                    ex is IOException
                    or ObjectDisposedException
                    or JsonPipeRpcException)
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
                using var timeout =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    await process.WaitForExitAsync(timeout.Token);
                }
                catch (OperationCanceledException) when (
                    !cancellationToken.IsCancellationRequested)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(cancellationToken);
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
            _logger.LogError(
                ex,
                "Failed to handle PluginHost {PluginId} exit.",
                PluginId);
        }
#pragma warning restore CA1031
    }

    private async Task HandleProcessExitedAsync(Process? exitedProcess)
    {
        await _gate.WaitAsync();
        try
        {
            if (_intentionalStop
                || _disposed
                || exitedProcess is null
                || !ReferenceEquals(exitedProcess, _process))
            {
                return;
            }

            var exitCode = exitedProcess.ExitCode;
            await StopProgressPumpAsync();
            CleanupConnection();
            Exited?.Invoke(
                this,
                new PluginHostSessionExitedEventArgs(exitCode));
        }
        finally
        {
            _gate.Release();
        }
    }

    private void CleanupConnection()
    {
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
    }

    private void StartProgressPump()
    {
        _progressPumpCancellation?.Cancel();
        _progressPumpCancellation?.Dispose();
        _progressPumpCancellation = new CancellationTokenSource();
        _progressPumpTask = PumpPlaybackProgressAsync(
            _progressPumpCancellation.Token);
    }

    private async Task StopProgressPumpAsync()
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
        try
        {
            if (task is not null)
            {
                await task;
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
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

            await StopCoreAsync(CancellationToken.None);
            _disposed = true;
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
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

internal sealed class PluginHostSessionExitedEventArgs(int exitCode)
    : EventArgs
{
    public int ExitCode { get; } = exitCode;
}
