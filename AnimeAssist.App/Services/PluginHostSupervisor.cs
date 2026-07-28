using AniMeido.PluginProtocol;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.IO.Pipes;

namespace AniMeido.App.Services;

public sealed class PluginHostSupervisor : IAsyncDisposable
{
    private readonly PluginPackageManager _packageManager;
    private readonly PluginContributionRegistry _contributions;
    private readonly HostedAnimePlaybackLauncher _playbackLauncher;
    private readonly ILogger<PluginHostSupervisor> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private JsonPipeRpcClient? _rpc;
    private NamedPipeServerStream? _pipe;
    private bool _intentionalStop;
    private bool _automaticRestartUsed;
    private bool _disposed;

    public PluginHostSupervisor(
        PluginPackageManager packageManager,
        PluginContributionRegistry contributions,
        HostedAnimePlaybackLauncher playbackLauncher,
        ILogger<PluginHostSupervisor> logger)
    {
        _packageManager = packageManager;
        _contributions = contributions;
        _playbackLauncher = playbackLauncher;
        _logger = logger;
        _contributions.CommandInvoker = InvokeCommandAsync;
        _playbackLauncher.Attach(this);
    }

    public event EventHandler? StatusChanged;

    public string StatusText { get; private set; } = "未启动";

    public bool IsRunning => _process is { HasExited: false } && _rpc is not null;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await StartCoreAsync(cancellationToken);
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

    public async Task<bool> HasActivePluginUiAsync(
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

    public async Task ReloadAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await StopCoreAsync(cancellationToken);
            await StartCoreAsync(cancellationToken);
            _packageManager.MarkReloadApplied();
            _automaticRestartUsed = false;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task InvokeCommandAsync(string pluginId, string commandId)
    {
        var rpc = _rpc
            ?? throw new InvalidOperationException("PluginHost 尚未连接。");
        return rpc.InvokeAsync(
            PluginHostRpcTargetNames.InvokeCommandAsync,
            [pluginId, commandId]);
    }

    public Task LaunchAnimePlaybackAsync(
        AnimePlaybackRequest request,
        CancellationToken cancellationToken)
    {
        var rpc = _rpc
            ?? throw new InvalidOperationException("PluginHost 尚未连接。");
        return rpc.InvokeAsync(
            PluginHostRpcTargetNames.LaunchAnimePlaybackAsync,
            [request],
            cancellationToken);
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsRunning)
        {
            return;
        }

        var directories = await _packageManager.PrepareForStartupAsync(
            cancellationToken);
        var descriptors = directories
            .Select(directory => new HostedPluginDescriptor(
                directory,
                PluginManifest.LoadFromFile(
                    Path.Combine(directory, "plugin.json"))
                    ?? throw new PluginOperationException(
                        "已验证插件缺少 plugin.json。")))
            .ToArray();
        if (descriptors.Length == 0)
        {
            ApplySnapshot(new PluginHostSnapshot([], [], []));
            SetStatus("没有已启用的可选插件");
            return;
        }

        var hostPath = ResolveHostPath();
        if (!File.Exists(hostPath))
        {
            ApplySnapshot(new PluginHostSnapshot([], [], []));
            SetStatus("PluginHost 可执行文件不存在");
            _logger.LogError(
                "PluginHost executable was not found at {HostPath}.",
                hostPath);
            return;
        }

        var pipeName = $"AniMeido.PluginHost.{Environment.ProcessId}.{Guid.NewGuid():N}";
        _pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        _process = Process.Start(new ProcessStartInfo
        {
            FileName = hostPath,
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
        var appVersion = typeof(PluginHostSupervisor).Assembly
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
            [descriptors],
            timeout.Token);
        if (snapshot.Failures.Count > 0)
        {
            await _packageManager.RecordLoadFailuresAsync(
                snapshot.Failures.ToDictionary(
                    failure => failure.PluginId,
                    failure => failure.Message,
                    StringComparer.OrdinalIgnoreCase),
                timeout.Token);
        }
        ApplySnapshot(snapshot);
        SetStatus(snapshot.Failures.Count == 0
            ? "运行中"
            : $"运行中，{snapshot.Failures.Count} 个插件加载失败");
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        _intentionalStop = true;
        try
        {
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
                    _logger.LogDebug(ex, "PluginHost ended before shutdown acknowledged.");
                }
            }

            if (_process is { HasExited: false } process)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
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
            ApplySnapshot(new PluginHostSnapshot([], [], []));
            _intentionalStop = false;
        }
    }

    private async void OnProcessExited(object? sender, EventArgs e)
    {
        if (_intentionalStop || _disposed)
        {
            return;
        }

        CleanupConnection();
        ApplySnapshot(new PluginHostSnapshot([], [], []));
        if (_automaticRestartUsed)
        {
            SetStatus("已停止：PluginHost 连续崩溃，请手动重载");
            return;
        }

        _automaticRestartUsed = true;
        SetStatus("PluginHost 异常退出，正在自动恢复");
        try
        {
            await StartAsync();
        }
#pragma warning disable CA1031 // A failed optional host restart must not crash the App.
        catch (Exception ex)
        {
            _logger.LogError(ex, "PluginHost automatic restart failed.");
            SetStatus($"PluginHost 自动恢复失败：{ex.Message}");
        }
#pragma warning restore CA1031
    }

    private void ApplySnapshot(PluginHostSnapshot snapshot)
    {
        _contributions.SetHostedCommands(snapshot.NavigationCommands);
        _playbackLauncher.SetAvailable(
            snapshot.Capabilities.Contains(
                PluginHostProtocol.AnimePlaybackCapability,
                StringComparer.Ordinal));
        foreach (var failure in snapshot.Failures)
        {
            _logger.LogError(
                "Plugin {PluginId} failed in PluginHost: {Message}",
                failure.PluginId,
                failure.Message);
        }
    }

    private void SetStatus(string status)
    {
        StatusText = status;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void CleanupConnection()
    {
        if (_process is not null)
        {
            _process.Exited -= OnProcessExited;
            _process.Dispose();
            _process = null;
        }

        if (_rpc is not null)
        {
            _rpc.Dispose();
        }
        _rpc = null;
        _pipe?.Dispose();
        _pipe = null;
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

            await StopCoreAsync(CancellationToken.None);
            _disposed = true;
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}

internal static class PluginHostRpcTargetNames
{
    public const string HandshakeAsync = "HandshakeAsync";
    public const string InitializeAsync = "InitializeAsync";
    public const string InvokeCommandAsync = "InvokeCommandAsync";
    public const string LaunchAnimePlaybackAsync = "LaunchAnimePlaybackAsync";
    public const string GetRuntimeStateAsync = "GetRuntimeStateAsync";
    public const string ShutdownAsync = "ShutdownAsync";
}
