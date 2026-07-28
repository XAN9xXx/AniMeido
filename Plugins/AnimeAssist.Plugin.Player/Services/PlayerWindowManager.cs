using AniMeido.Contracts;
using AniMeido.Contracts.Playback;
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
internal sealed class PlayerWindowManager : IAnimePlaybackLauncher
{
    private readonly OnlineSourceCatalog _sourceCatalog;
    private readonly SourcePackageInstaller _sourcePackageInstaller;
    private readonly SourceSubscriptionService _subscriptionService;
    private readonly EasyPreferenceStore _preferenceStore;
    private readonly PlayerRuntimeSettingsStore _runtimeSettings;
    private readonly WebMediaResolver _webResolver;
    private readonly PlaybackDiagnosticRecorder _diagnostics;
    private readonly PlayerExperienceSettingsStore _experienceSettings;
    private PlayerWindow? _playerWindow;
    private Window? _mainWindow;
    private bool _isAppClosing;

    public PlayerWindowManager(
        OnlineSourceCatalog sourceCatalog,
        SourcePackageInstaller sourcePackageInstaller,
        SourceSubscriptionService subscriptionService,
        EasyPreferenceStore preferenceStore,
        PlayerRuntimeSettingsStore runtimeSettings,
        WebMediaResolver webResolver,
        PlaybackDiagnosticRecorder diagnostics,
        PlayerExperienceSettingsStore experienceSettings)
    {
        _sourceCatalog = sourceCatalog;
        _sourcePackageInstaller = sourcePackageInstaller;
        _subscriptionService = subscriptionService;
        _preferenceStore = preferenceStore;
        _runtimeSettings = runtimeSettings;
        _webResolver = webResolver;
        _diagnostics = diagnostics;
        _experienceSettings = experienceSettings;
    }

    public async Task LaunchAsync(
        AnimePlaybackContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (_isAppClosing)
        {
            return;
        }

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

        _mainWindow = AppServices.MainWindow as Window;
        if (_mainWindow is not null)
        {
            _mainWindow.Closed += OnMainWindowClosed;
        }

        _playerWindow = new PlayerWindow(
            context,
            _sourceCatalog,
            _sourcePackageInstaller,
            _subscriptionService,
            _preferenceStore,
            _runtimeSettings,
            _webResolver,
            _diagnostics,
            _experienceSettings);
        _playerWindow.Closed += OnPlayerWindowClosed;
        _playerWindow.Activate();
    }

    private void OnMainWindowClosed(object sender, WindowEventArgs args)
    {
        _isAppClosing = true;
        var playerWindow = _playerWindow;
        DetachWindow();
        playerWindow?.Close();
    }

    private void OnPlayerWindowClosed(object sender, WindowEventArgs args)
        => DetachWindow();

    private void DetachWindow()
    {
        if (_playerWindow is not null)
        {
            _playerWindow.Closed -= OnPlayerWindowClosed;
            _playerWindow = null;
        }

        if (_mainWindow is not null)
        {
            _mainWindow.Closed -= OnMainWindowClosed;
            _mainWindow = null;
        }
    }
}
