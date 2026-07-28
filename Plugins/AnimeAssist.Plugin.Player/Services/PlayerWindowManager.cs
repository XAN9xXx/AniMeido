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
internal sealed class PlayerWindowManager : IAnimePlaybackLauncher, IDisposable
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
            _sourcePackageInstaller,
            _subscriptionService,
            _preferenceStore,
            _runtimeSettings,
            _webResolver,
            _diagnostics,
            _experienceSettings,
            _playbackProgressReporter);
        _playerWindow.Closed += OnPlayerWindowClosed;
        _playerWindow.Activate();
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

    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var playerWindow = _playerWindow;
        DetachWindow();
        if (playerWindow is null)
        {
            return;
        }

        try
        {
            playerWindow.Close();
        }
        catch (Exception ex)
            when (ex is COMException or InvalidOperationException)
        {
            // The WinUI shutdown path may already have invalidated the window.
        }
    }
}
