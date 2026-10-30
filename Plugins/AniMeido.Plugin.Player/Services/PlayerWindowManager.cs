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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
    IAsyncDisposable
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
    private readonly PlayerPublicIdBoundary _publicIds;
    private readonly ILogger<PlayerWindowManager> _logger;
    private PlayerWindow? _playerWindow;
    private SourceManagementWindow? _settingsWindow;
    private bool _endingSession;
    private bool _disposed;
    private Task? _endSessionTask;

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
        IAnimePlaybackProgressReporter playbackProgressReporter,
        SourceMappingStore? mappingStore = null,
        ILogger<PlayerWindowManager>? logger = null)
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
        _publicIds = new PlayerPublicIdBoundary(mappingStore ?? sourceCatalog.MappingStore, experienceSettings);
        _logger = logger ?? NullLogger<PlayerWindowManager>.Instance;
    }

    public async Task LaunchAsync(
        AnimePlaybackContext context,
        CancellationToken cancellationToken = default)
    {
        EnsureSessionAvailable(cancellationToken);
        ArgumentNullException.ThrowIfNull(context);
        var preparation = await _publicIds.PrepareOpenAsync(context.AnimeId, cancellationToken);
        var experience = preparation.ReadResult;
        if (experience.FallbackReason is { } reason)
            _logger.LogWarning(reason,
                "Failed to read player settings; using defaults for {Operation}.",
                "playback launch");
        await _sourceCatalog.EnsureLoadedAsync(cancellationToken);
        EnsureSessionAvailable(cancellationToken);
        if (_playerWindow is not null)
        {
            var existingWindow = _playerWindow;
            try
            {
                if (!await existingWindow.EnsureWebBindingAsync(cancellationToken))
                {
                    await existingWindow.CloseAsync();
                    DetachWindow(existingWindow);
                    return;
                }
                EnsureSessionAvailable(cancellationToken);
                await existingWindow.ShowAnimeAsync(context, preparation.Identity, cancellationToken);
                if (!existingWindow.IsClosing && ReferenceEquals(_playerWindow, existingWindow))
                {
                    existingWindow.Activate();
                }
                return;
            }
            catch (COMException)
            {
                DetachWindow(existingWindow);
            }
        }

        EnsureSessionAvailable(cancellationToken);
        var window = new PlayerWindow(
            context,
            _sourceCatalog,
            () => OpenSettingsAsync(PlayerSettingsId),
            _webResolver,
            _diagnostics,
            _experienceSettings,
            _publicIds,
            _playbackProgressReporter,
            preparation.Identity,
            _logger);
        _playerWindow = window;
        window.Closed += OnPlayerWindowClosed;
        try
        {
            var attached = await window.EnsureWebBindingAsync(cancellationToken);
            EnsureSessionAvailable(cancellationToken);
            if (attached && !window.IsClosing && ReferenceEquals(_playerWindow, window))
            {
                window.Activate();
                return;
            }
        }
#pragma warning disable CA1031 // 创建但尚未激活的窗口仍由本次启动完整收尾。
        catch (Exception launchFailure)
        {
            try
            {
                await CloseUnactivatedWindowAsync(window);
            }
            catch (Exception closeFailure)
            {
                throw new AggregateException("播放器启动失败，且窗口未能完成关闭。", launchFailure, closeFailure);
            }
            throw;
        }
#pragma warning restore CA1031
        await CloseUnactivatedWindowAsync(window);
    }

    private async Task CloseUnactivatedWindowAsync(PlayerWindow window)
    {
        // 启动取消只清理本次窗口，不触发会话退出而误关已有的源管理窗口。
        window.Closed -= OnPlayerWindowClosed;
        await window.CloseAsync();
        DetachWindow(window);
    }

    public Task<ActiveAnimePlaybackContext?> GetActiveContextAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_playerWindow?.GetActiveContextSnapshot());
    }

    public async Task OpenSettingsAsync(
        string settingsId,
        CancellationToken cancellationToken = default)
    {
        EnsureSessionAvailable(cancellationToken);
        if (!string.Equals(
            settingsId,
            PlayerSettingsId,
            StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"未知播放器设置入口：{settingsId}",
                nameof(settingsId));
        }

        var experience = await _publicIds.PrepareSettingsAsync(cancellationToken);
        if (experience.FallbackReason is { } reason)
            _logger.LogWarning(reason,
                "Failed to read player settings; using defaults for {Operation}.",
                "settings launch");
        await _sourceCatalog.EnsureLoadedAsync(cancellationToken);
        EnsureSessionAvailable(cancellationToken);
        OpenSourceManagement();
    }

    private void EnsureSessionAvailable(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_endingSession)
        {
            throw new InvalidOperationException("播放器会话正在退出，请稍后重试。");
        }
        cancellationToken.ThrowIfCancellationRequested();
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
            _webResolver,
            EndSessionFromWindowAsync,
            _logger);
        _settingsWindow.SourcesChanged += OnSourcesChanged;
        _settingsWindow.Closed += OnSettingsWindowClosed;
        _settingsWindow.Activate();
    }

    private async void OnPlayerWindowClosed(object sender, WindowEventArgs args)
    {
        DetachWindow();
        await EndSessionFromWindowAsync();
    }

    private void DetachWindow(PlayerWindow? expectedWindow = null)
    {
        if (expectedWindow is not null && !ReferenceEquals(_playerWindow, expectedWindow))
        {
            return;
        }
        if (_playerWindow is not null)
        {
            _playerWindow.Closed -= OnPlayerWindowClosed;
            _playerWindow = null;
        }

    }

    private async void OnSettingsWindowClosed(
        object sender,
        WindowEventArgs args)
    {
        DetachSettingsWindow();
        await EndSessionFromWindowAsync();
    }

    private async Task EndSessionFromWindowAsync()
    {
        try
        {
            await EndSessionAsync();
        }
#pragma warning disable CA1031 // 窗口事件负责报告关闭失败；宿主退出仍可等待同一个收尾任务。
        catch (Exception ex)
        {
            _diagnostics.Record("player-session", "close-failed",
                data: new Dictionary<string, object?>
                {
                    ["exception"] = ex.GetType().Name,
                    ["message"] = ex.Message,
                });
        }
#pragma warning restore CA1031
    }

    private Task EndSessionAsync()
    {
        if (_endSessionTask is not null)
            return _endSessionTask;

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _endSessionTask = completion.Task;
        _ = CompleteEndSessionAsync(completion);
        return completion.Task;
    }

    private async Task CompleteEndSessionAsync(TaskCompletionSource completion)
    {
        try
        {
            await EndSessionCoreAsync();
            completion.TrySetResult();
        }
#pragma warning disable CA1031 // 先公布共享任务，再把原始收尾结果交给事件与宿主。
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }
#pragma warning restore CA1031
    }

    private async Task EndSessionCoreAsync()
    {
        _endingSession = true;
        var playerWindow = _playerWindow;
        var settingsWindow = _settingsWindow;
        DetachSettingsWindow();
        await CloseWindowsAsync(
            settingsWindow is null ? null : settingsWindow.CloseAsync,
            playerWindow is null ? null : ClosePlayerAsync);

        async Task ClosePlayerAsync()
        {
            await playerWindow!.CloseAsync();
            DetachWindow(playerWindow);
        }
    }

    internal static async Task CloseWindowsAsync(Func<Task>? closeSettings, Func<Task>? closePlayer)
    {
        // 两个关闭入口都先发出取消；任一侧排空都可能依赖另一侧释放解析器。
        var settings = CloseAsync(closeSettings);
        var player = CloseAsync(closePlayer);
        var closing = Task.WhenAll(settings, player);
        try
        {
            await closing;
        }
#pragma warning disable CA1031 // 双方真实收尾后，将全部失败交给会话所有者。
        catch (Exception) when (closing.Exception is { } failure)
        {
            throw failure;
        }
#pragma warning restore CA1031

        static async Task CloseAsync(Func<Task>? close)
        {
            if (close is not null)
                await close();
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

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return new ValueTask(EndSessionAsync());
    }

    internal const string PlayerSettingsId =
        "AniMeido.Plugin.Player.settings";
}
