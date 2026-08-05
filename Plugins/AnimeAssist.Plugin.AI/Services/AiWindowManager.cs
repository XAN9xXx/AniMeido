using AniMeido.Contracts.Plugins;
using AniMeido.Plugin.AI.Views;
using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;

namespace AniMeido.Plugin.AI.Services;

internal sealed class AiWindowManager :
    IPluginCommandLauncher,
    IPluginSettingsLauncher,
    IDisposable
{
    internal const string OpenCommandId = "AniMeido.Plugin.AI.open";
    internal const string SettingsId = "AniMeido.Plugin.AI.settings";
    private readonly AiTaskCoordinator _coordinator;
    private readonly AiPluginPaths _paths;
    private readonly AiSettingsStore _settingsStore;
    private readonly DpapiSecretStore _secretStore;
    private readonly AiProviderRouter _providers;
    private AiWorkbenchWindow? _workbench;
    private AiSettingsWindow? _settings;
    private bool _endingSession;
    private bool _disposed;

    public AiWindowManager(
        AiTaskCoordinator coordinator,
        AiPluginPaths paths,
        AiSettingsStore settingsStore,
        DpapiSecretStore secretStore,
        AiProviderRouter providers)
    {
        _coordinator = coordinator;
        _paths = paths;
        _settingsStore = settingsStore;
        _secretStore = secretStore;
        _providers = providers;
    }

    public Task InvokeCommandAsync(
        string commandId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(commandId, OpenCommandId, StringComparison.Ordinal))
        {
            throw new ArgumentException($"未知 AI 命令：{commandId}", nameof(commandId));
        }

        if (_workbench is not null)
        {
            try
            {
                _workbench.Activate();
                return Task.CompletedTask;
            }
            catch (COMException)
            {
                DetachWorkbench();
            }
        }

        _workbench = new AiWorkbenchWindow(_coordinator, _paths);
        _workbench.Closed += OnWindowClosed;
        _workbench.Activate();
        return Task.CompletedTask;
    }

    public Task OpenSettingsAsync(
        string settingsId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(settingsId, SettingsId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"未知 AI 设置入口：{settingsId}",
                nameof(settingsId));
        }

        if (_settings is not null)
        {
            try
            {
                _settings.Activate();
                return Task.CompletedTask;
            }
            catch (COMException)
            {
                DetachSettings();
            }
        }

        _settings = new AiSettingsWindow(
            _settingsStore,
            _secretStore,
            _providers);
        _settings.Closed += OnWindowClosed;
        _settings.Activate();
        return Task.CompletedTask;
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
        => EndSession();

    private void EndSession()
    {
        if (_endingSession)
        {
            return;
        }

        _endingSession = true;
        var workbench = _workbench;
        var settings = _settings;
        DetachWorkbench();
        DetachSettings();
        TryClose(workbench);
        TryClose(settings);
        _ = Microsoft.UI.Dispatching.DispatcherQueue
            .GetForCurrentThread()
            .TryEnqueue(() => Application.Current.Exit());
    }

    private void DetachWorkbench()
    {
        if (_workbench is null)
        {
            return;
        }

        _workbench.Closed -= OnWindowClosed;
        _workbench = null;
    }

    private void DetachSettings()
    {
        if (_settings is null)
        {
            return;
        }

        _settings.Closed -= OnWindowClosed;
        _settings = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var workbench = _workbench;
        var settings = _settings;
        DetachWorkbench();
        DetachSettings();
        TryClose(workbench);
        TryClose(settings);
    }

    private static void TryClose(Window? window)
    {
        if (window is null)
        {
            return;
        }

        try
        {
            window.Close();
        }
        catch (Exception ex) when (
            ex is COMException or InvalidOperationException)
        {
        }
    }
}
