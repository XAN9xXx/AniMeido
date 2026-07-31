using AniMeido.Contracts.Playback;
using AniMeido.Contracts.Plugins;
using AniMeido.Plugin.Player.Diagnostics;
using AniMeido.Plugin.Player.Playback;
using AniMeido.Plugin.Player.Sources;
using AniMeido.Plugin.Player.Sources.EasyBangumi;
using AniMeido.Plugin.Player.Sources.Packages;
using AniMeido.Plugin.Player.Sources.Subscriptions;
using AniMeido.Plugin.Player.Sources.Web;
using AniMeido.Plugin.Player.Views;
using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;

namespace AniMeido.Plugin.Player.Services;

/// <summary>
/// Owns the online player window without exposing its implementation to BasePlugin.
/// </summary>
internal sealed class PlayerWindowManager :
    IAnimePlaybackLauncher,
    IActiveAnimePlaybackContextProvider,
    IPluginSettingsLauncher,
    IDisposable
{
    private readonly OnlineSourceCatalog _sourceCatalog;
    private readonly SourcePackageInstaller _sourcePackageInstaller;
    private readonly SourceSubscriptionService _subscriptionService;
    private readonly EasyPreferenceStore _preferenceStore;
    private readonly PlayerRuntimeSettingsStore _runtimeSettings;
    private readonly WebMediaResolver _webResolver;
    private readonly PlaybackDiagnosticRecorder _diagnostics;
    private readonly PlayerExperienceSettingsStore _experienceSettings;
    private readonly IAnimePlaybackProgressReporter _playbackProgressReporter;
    private PlayerWindow? _playerWindow;
    private SourceManagementWindow? _settingsWindow;
    private bool _endingSession;
    private bool _disposed;

    public bool IsAvailable => true;

    public event EventHandler? AvailabilityChanged
    {
        add { }
        remove { }
    }

    public PlayerWindowManager(
        OnlineSourceCatalog sourceCatalog,
        SourcePackageInstaller sourcePackageInstaller,
        SourceSubscriptionService subscriptionService,
        EasyPreferenceStore preferenceStore,
        PlayerRuntimeSettingsStore runtimeSettings,
        WebMediaResolver webResolver,
        PlaybackDiagnosticRecorder diagnostics,
        PlayerExperienceSettingsStore experienceSettings,
        IAnimePlaybackProgressReporter playbackProgressReporter)
    {
        _sourceCatalog = sourceCatalog;
        _sourcePackageInstaller = sourcePackageInstaller;
        _subscriptionService = subscriptionService;
        _preferenceStore = preferenceStore;
        _runtimeSettings = runtimeSettings;
        _webResolver = webResolver;
        _diagnostics = diagnostics;
        _experienceSettings = experienceSettings;
        _playbackProgressReporter = playbackProgressReporter;
    }

    public async Task LaunchAsync(
        AnimePlaybackContext context,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_endingSession)
        {
            throw new InvalidOperationException(
                "播放器会话正在退出，请稍后重试。");
        }

        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (_playerWindow is not null)
        {
            try
            {
                await _playerWindow.ShowAnimeAsync(context, cancellationToken);
                _playerWindow.Activate();
                return;
            }
            catch (COMException)
            {
                DetachWindow();
            }
        }

        _playerWindow = new PlayerWindow(
            context,
            _sourceCatalog,
            OpenSourceManagement,
            _webResolver,
            _diagnostics,
            _experienceSettings,
            _playbackProgressReporter);
        _playerWindow.Closed += OnPlayerWindowClosed;
        _playerWindow.Activate();
    }

    public Task<ActiveAnimePlaybackContext?> GetActiveContextAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_playerWindow?.GetActiveContextSnapshot());
    }

    public Task OpenSettingsAsync(
        string settingsId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_endingSession)
        {
            throw new InvalidOperationException(
                "播放器会话正在退出，请稍后重试。");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(
            settingsId,
            PlayerSettingsId,
            StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"未知播放器设置入口：{settingsId}",
                nameof(settingsId));
        }

        OpenSourceManagement();
        return Task.CompletedTask;
    }

    private void OpenSourceManagement()
    {
        if (_settingsWindow is not null)
        {
            try
            {
                _settingsWindow.Activate();
                return;
            }
            catch (COMException)
            {
                DetachSettingsWindow();
            }
        }

        _settingsWindow = new SourceManagementWindow(
            _sourceCatalog,
            _sourcePackageInstaller,
            _subscriptionService,
            _preferenceStore,
            _runtimeSettings,
            _webResolver);
        _settingsWindow.SourcesChanged += OnSourcesChanged;
        _settingsWindow.Closed += OnSettingsWindowClosed;
        _settingsWindow.Activate();
    }

    private void OnPlayerWindowClosed(object sender, WindowEventArgs args)
    {
        DetachWindow();
        EndSession();
    }

    private void DetachWindow()
    {
        if (_playerWindow is not null)
        {
            _playerWindow.Closed -= OnPlayerWindowClosed;
            _playerWindow = null;
        }

    }

    private void OnSettingsWindowClosed(
        object sender,
        WindowEventArgs args)
    {
        DetachSettingsWindow();
        EndSession();
    }

    private void EndSession()
    {
        if (_endingSession)
        {
            return;
        }

        _endingSession = true;
        var playerWindow = _playerWindow;
        var settingsWindow = _settingsWindow;
        DetachWindow();
        DetachSettingsWindow();
        if (settingsWindow is not null)
        {
            TryClose(settingsWindow);
        }

        if (playerWindow is not null)
        {
            TryClose(playerWindow);
        }
    }

    private async void OnSourcesChanged(object? sender, EventArgs args)
    {
        if (_playerWindow is null)
        {
            return;
        }

        await _playerWindow.RefreshSourcesAsync();
    }

    private void DetachSettingsWindow()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.SourcesChanged -= OnSourcesChanged;
            _settingsWindow.Closed -= OnSettingsWindowClosed;
            _settingsWindow = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var playerWindow = _playerWindow;
        var settingsWindow = _settingsWindow;
        DetachWindow();
        if (settingsWindow is not null)
        {
            DetachSettingsWindow();
            TryClose(settingsWindow);
        }

        if (playerWindow is not null)
        {
            TryClose(playerWindow);
        }
    }

    private static void TryClose(Window window)
    {
        try
        {
            window.Close();
        }
        catch (Exception ex)
            when (ex is COMException or InvalidOperationException)
        {
            // The WinUI shutdown path may already have invalidated the window.
        }
    }

    internal const string PlayerSettingsId =
        "AniMeido.Plugin.Player.settings";
}
