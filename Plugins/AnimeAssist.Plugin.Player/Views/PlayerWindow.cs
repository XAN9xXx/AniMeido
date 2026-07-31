using AniMeido.Contracts.Playback;
using AniMeido.Plugin.Player.Diagnostics;
using AniMeido.Plugin.Player.Models;
using AniMeido.Plugin.Player.Playback;
using AniMeido.Plugin.Player.Sources;
using AniMeido.Plugin.Player.Sources.Web;
using Microsoft.UI.Dispatching;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Diagnostics;
using Windows.Graphics;
using Windows.System;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace AniMeido.Plugin.Player.Views;

internal sealed class PlayerWindow : Window
{
    private readonly OnlineSourceCatalog _sourceCatalog;
    private readonly Action _openSourceManagement;
    private readonly WebMediaResolver _webResolver;
    private readonly PlaybackDiagnosticRecorder _diagnostics;
    private readonly Grid _root = new();
    private readonly Border _videoSurface = new();
    private readonly ComboBox _episodeList = new();
    private readonly ComboBox _routeList = new();
    private readonly ComboBox _speedList = new();
    private readonly TextBlock _animeTitle = new();
    private readonly TextBlock _status = new();
    private readonly ProgressRing _loading = new();
    private readonly Button _playButton = new();
    private readonly Slider _progressSlider = new();
    private readonly Slider _volumeSlider = new();
    private readonly TextBlock _timeText = new();
    private readonly Button _muteButton = new();
    private readonly Button _nextButton = new();
    private readonly Button _fullScreenButton = new();
    private readonly Grid _heading = new();
    private readonly Grid _selectorBar = new();
    private readonly Grid _footer = new();
    private readonly PlayerExperienceSettingsStore _experienceSettings;
    private readonly IAnimePlaybackProgressReporter _playbackProgressReporter;
    private readonly DispatcherTimer _playbackTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(500),
    };
    private PlaybackSession _session;
    private CancellationTokenSource? _sourceLoadCancellation;
    private CancellationTokenSource? _playbackResolutionCancellation;
    private NativeVideoHost? _nativeVideoHost;
    private LibMpvClient? _mpv;
    private string? _nativePlayerError;
    private bool _nativePlayerInitialized;
    private bool _updatingControls;
    private bool _isFullScreen;
    private bool _sourceLookupInProgress;
    private bool _isClosing;
    private bool _suppressSelectionPlayback;
    private bool _isChangingEpisode;
    private bool _isProgressDragging;
    private bool _isMuted;
    private bool _reachedEnd;
    private double _playbackSpeed = 1;
    private double? _pendingResumePosition;
    private double _lastPlaybackPosition;
    private double _lastPlaybackDuration;
    private bool _progressReportedForEpisode;
    private DateTimeOffset _resolutionStartedAtUtc;
    private PlaybackViewState _viewState = PlaybackViewState.Idle;
    private PlayerExperienceSettings _experience = new();
    private IReadOnlyList<PlayerEpisodeGroup> _episodeGroups = [];
    private AppWindow? _appWindow;
    private readonly object _activeContextSync = new();
    private int _activeAnimeId;
    private string _activeAnimeTitle = string.Empty;
    private bool _hasActivePlayback;
    private int? _activeEpisodeNumber;
    private double? _activePositionSeconds;
    private DateTimeOffset _activeContextObservedAt = DateTimeOffset.UtcNow;

    public PlayerWindow(
        AnimePlaybackContext anime,
        OnlineSourceCatalog sourceCatalog,
        Action openSourceManagement,
        WebMediaResolver webResolver,
        PlaybackDiagnosticRecorder diagnostics,
        PlayerExperienceSettingsStore experienceSettings,
        IAnimePlaybackProgressReporter playbackProgressReporter)
    {
        ArgumentNullException.ThrowIfNull(anime);
        _sourceCatalog = sourceCatalog;
        _openSourceManagement = openSourceManagement;
        _webResolver = webResolver;
        _diagnostics = diagnostics;
        _experienceSettings = experienceSettings;
        _playbackProgressReporter = playbackProgressReporter;
        webResolver.AttachUiThread(
            Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread(),
            WindowNative.GetWindowHandle(this));
        _session = new PlaybackSession(anime);
        _activeAnimeId = anime.AnimeId;
        _activeAnimeTitle = anime.Title;
        Title = "AniMeido 在线播放器";
        Content = BuildLayout();
        ResizeWindow();
        _playbackTimer.Tick += OnPlaybackTimerTick;
        _root.KeyDown += OnRootKeyDown;
        Activated += OnWindowActivated;
        Closed += OnWindowClosed;
    }

    public async Task ShowAnimeAsync(
        AnimePlaybackContext anime,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(anime);
        if (_session.AnimeContext == anime)
        {
            return;
        }

        CancelPlaybackResolution();
        _session.ChangeAnime(anime);
        lock (_activeContextSync)
        {
            _activeAnimeId = anime.AnimeId;
            _activeAnimeTitle = anime.Title;
            _activeEpisodeNumber = null;
            _activePositionSeconds = null;
            _hasActivePlayback = false;
            _activeContextObservedAt = DateTimeOffset.UtcNow;
        }
        ResetProgressReport();
        await LoadEpisodesAsync(cancellationToken);
    }

    internal ActiveAnimePlaybackContext? GetActiveContextSnapshot()
    {
        lock (_activeContextSync)
        {
            if (!_hasActivePlayback)
            {
                return null;
            }

            return new ActiveAnimePlaybackContext(
                _activeAnimeId,
                _activeAnimeTitle,
                _activeEpisodeNumber,
                _activePositionSeconds,
                _activeContextObservedAt);
        }
    }

    private UIElement BuildLayout()
    {
        _root.Background = PlayerVisualStyles.WindowBackground;
        _root.Padding = new Thickness(20, 18, 20, 20);
        _root.RowSpacing = 12;
        _root.IsTabStop = true;
        _root.RowDefinitions.Add(
            new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition());
        _root.RowDefinitions.Add(
            new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(
            new RowDefinition { Height = GridLength.Auto });

        _heading.ColumnSpacing = 12;
        _heading.ColumnDefinitions.Add(new ColumnDefinition());
        _heading.ColumnDefinitions.Add(
            new ColumnDefinition { Width = GridLength.Auto });

        var titlePanel = new StackPanel { Spacing = 3 };
        titlePanel.Children.Add(
            PlayerVisualStyles.CreatePageTitle("在线播放"));
        _animeTitle.FontSize = 13;
        _animeTitle.Opacity = 0.68;
        titlePanel.Children.Add(_animeTitle);
        _heading.Children.Add(titlePanel);

        _loading.Width = 28;
        _loading.Height = 28;
        _loading.IsActive = true;
        Grid.SetColumn(_loading, 1);
        _heading.Children.Add(_loading);
        _root.Children.Add(_heading);

        _videoSurface.Background = new SolidColorBrush(Colors.Black);
        _videoSurface.CornerRadius = new CornerRadius(14);
        _videoSurface.BorderBrush = PlayerVisualStyles.SurfaceStroke;
        _videoSurface.BorderThickness = new Thickness(1);
        _videoSurface.SizeChanged += OnVideoSurfaceSizeChanged;
        Grid.SetRow(_videoSurface, 1);
        _root.Children.Add(_videoSurface);

        _selectorBar.Background =
            PlayerVisualStyles.SurfaceBackground;
        _selectorBar.BorderBrush = PlayerVisualStyles.SurfaceStroke;
        _selectorBar.BorderThickness = new Thickness(1);
        _selectorBar.CornerRadius = new CornerRadius(12);
        _selectorBar.Padding = new Thickness(12, 9, 12, 9);
        _selectorBar.ColumnSpacing = 8;
        _selectorBar.ColumnDefinitions.Add(
            new ColumnDefinition { Width = GridLength.Auto });
        _selectorBar.ColumnDefinitions.Add(
            new ColumnDefinition { Width = new GridLength(220) });
        _selectorBar.ColumnDefinitions.Add(
            new ColumnDefinition { Width = GridLength.Auto });
        _selectorBar.ColumnDefinitions.Add(new ColumnDefinition());
        _selectorBar.Children.Add(new TextBlock
        {
            Text = "剧集",
            VerticalAlignment = VerticalAlignment.Center,
        });
        _episodeList.MinWidth = 150;
        _episodeList.HorizontalAlignment = HorizontalAlignment.Stretch;
        _episodeList.SelectionChanged += OnEpisodeSelectionChanged;
        Grid.SetColumn(_episodeList, 1);
        _selectorBar.Children.Add(_episodeList);
        var routeLabel = new TextBlock
        {
            Text = "线路",
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(routeLabel, 2);
        _selectorBar.Children.Add(routeLabel);
        _routeList.HorizontalAlignment = HorizontalAlignment.Stretch;
        _routeList.DisplayMemberPath =
            nameof(SourceEpisodeEntry.RouteDisplayText);
        _routeList.SelectionChanged += OnRouteSelectionChanged;
        Grid.SetColumn(_routeList, 3);
        _selectorBar.Children.Add(_routeList);
        Grid.SetRow(_selectorBar, 2);
        _root.Children.Add(_selectorBar);

        _footer.Background = PlayerVisualStyles.SurfaceBackground;
        _footer.BorderBrush = PlayerVisualStyles.SurfaceStroke;
        _footer.BorderThickness = new Thickness(1);
        _footer.CornerRadius = new CornerRadius(12);
        _footer.Padding = new Thickness(12);
        _footer.ColumnSpacing = 8;
        _footer.ColumnDefinitions.Add(new ColumnDefinition());
        _footer.ColumnDefinitions.Add(
            new ColumnDefinition { Width = GridLength.Auto });
        _footer.RowDefinitions.Add(
            new RowDefinition { Height = GridLength.Auto });
        _footer.RowDefinitions.Add(
            new RowDefinition { Height = GridLength.Auto });
        _footer.RowDefinitions.Add(
            new RowDefinition { Height = GridLength.Auto });

        _progressSlider.Minimum = 0;
        _progressSlider.Maximum = 1;
        _progressSlider.StepFrequency = 1;
        _progressSlider.IsEnabled = false;
        _progressSlider.ValueChanged += OnProgressValueChanged;
        _progressSlider.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler(OnProgressPointerPressed),
            true);
        _progressSlider.AddHandler(
            UIElement.PointerReleasedEvent,
            new PointerEventHandler(OnProgressPointerReleased),
            true);
        Grid.SetColumnSpan(_progressSlider, 2);
        _footer.Children.Add(_progressSlider);

        var primaryControls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7,
        };
        var backwardButton = CreateControlButton("−10 秒", OnSeekBackward);
        primaryControls.Children.Add(backwardButton);
        _playButton.Content = "播放";
        _playButton.IsEnabled = false;
        _playButton.Click += OnPlayClick;
        PlayerVisualStyles.StyleButton(
            _playButton,
            PlayerButtonTone.Primary);
        primaryControls.Children.Add(_playButton);
        primaryControls.Children.Add(
            CreateControlButton("+10 秒", OnSeekForward));
        _nextButton.Content = "下一集";
        _nextButton.IsEnabled = false;
        _nextButton.Click += OnNextEpisodeClick;
        PlayerVisualStyles.StyleButton(_nextButton);
        primaryControls.Children.Add(_nextButton);
        Grid.SetRow(primaryControls, 1);
        _footer.Children.Add(primaryControls);

        var playbackDetails = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _timeText.Text = "00:00 / 00:00";
        _timeText.MinWidth = 100;
        playbackDetails.Children.Add(_timeText);
        _muteButton.Content = "音量";
        _muteButton.Click += OnMuteClick;
        PlayerVisualStyles.StyleButton(_muteButton);
        playbackDetails.Children.Add(_muteButton);
        _volumeSlider.Minimum = 0;
        _volumeSlider.Maximum = 100;
        _volumeSlider.Value = 100;
        _volumeSlider.Width = 100;
        _volumeSlider.ValueChanged += OnVolumeValueChanged;
        _volumeSlider.AddHandler(
            UIElement.PointerReleasedEvent,
            new PointerEventHandler(OnPreferenceControlReleased),
            true);
        playbackDetails.Children.Add(_volumeSlider);
        _speedList.Width = 82;
        _speedList.ItemsSource = new[] { "0.5×", "0.75×", "1×", "1.25×", "1.5×", "2×" };
        _speedList.SelectedIndex = 2;
        _speedList.SelectionChanged += OnSpeedSelectionChanged;
        playbackDetails.Children.Add(_speedList);
        _fullScreenButton.Content = "全屏";
        _fullScreenButton.Click += OnFullScreenClick;
        PlayerVisualStyles.StyleButton(_fullScreenButton);
        playbackDetails.Children.Add(_fullScreenButton);
        playbackDetails.Children.Add(
            CreateControlButton("更多", OnMoreClick));
        Grid.SetColumn(playbackDetails, 1);
        Grid.SetRow(playbackDetails, 1);
        _footer.Children.Add(playbackDetails);

        _status.Text = "正在查找可用播放源…";
        _status.FontSize = 12;
        _status.Opacity = 0.8;
        _status.TextWrapping = TextWrapping.Wrap;
        _status.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumnSpan(_status, 2);
        Grid.SetRow(_status, 2);
        _footer.Children.Add(_status);
        Grid.SetRow(_footer, 3);
        _root.Children.Add(_footer);

        RenderAnimeContext();
        return _root;
    }

    private static Button CreateControlButton(
        string text,
        RoutedEventHandler handler)
    {
        var button = new Button
        {
            Content = text,
        };
        PlayerVisualStyles.StyleButton(button);
        button.Click += handler;
        return button;
    }

    private async void OnWindowActivated(
        object sender,
        WindowActivatedEventArgs args)
    {
        if (!_nativePlayerInitialized)
        {
            try
            {
                await LoadExperienceAsync();
                InitializeNativePlayer();
                await LoadEpisodesAsync(CancellationToken.None);
            }
#pragma warning disable CA1031 // Native player startup is recoverable in its own window.
            catch (Exception ex)
            {
                _nativePlayerError = $"播放器初始化失败：{ex.Message}";
                _status.Text = _nativePlayerError;
                _loading.IsActive = false;
                _mpv?.Dispose();
                _mpv = null;
                _nativeVideoHost?.Dispose();
                _nativeVideoHost = null;
            }
#pragma warning restore CA1031
        }
    }

    private void InitializeNativePlayer()
    {
        _nativePlayerInitialized = true;
        var windowHandle = WindowNative.GetWindowHandle(this);
        _nativeVideoHost = new NativeVideoHost(windowHandle);
        if (!LibMpvClient.TryCreate(
            _nativeVideoHost.Handle,
            _diagnostics,
            out _mpv,
            out var error))
        {
            _nativeVideoHost.Dispose();
            _nativeVideoHost = null;
            _nativePlayerError = error;
            _status.Text = _nativePlayerError;
            return;
        }

        var player = _mpv
            ?? throw new InvalidOperationException(
                "libmpv 初始化成功但未返回播放器实例。");
        player.FileLoaded += OnMpvFileLoaded;
        player.PlaybackEnded += OnMpvPlaybackEnded;
        player.PlaybackFailed += OnMpvPlaybackFailed;
        _playbackTimer.Start();
        UpdateVideoBounds();
    }

    private void OnMpvFileLoaded(object? sender, EventArgs e)
        => _ = DispatcherQueue.TryEnqueue(HandleMpvFileLoadedAsync);

    private async void HandleMpvFileLoadedAsync()
    {
        if (_isClosing)
        {
            return;
        }

        if (_session.Episode is { } episode)
        {
            await TryRecordRouteResultAsync(
                _session.AnimeContext.AnimeId,
                PlayerEpisodeGroup.GetRouteKey(episode),
                succeeded: true,
                DateTimeOffset.UtcNow - _resolutionStartedAtUtc);
        }

        SetPlaybackState(
            PlaybackViewState.Playing,
            GetNowPlayingText());
    }

    private void OnMpvPlaybackEnded(object? sender, EventArgs e)
        => _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (!_isClosing)
            {
                _reachedEnd = true;
                ReportProgressIfEligible(reachedNaturalEnd: true);
                SetPlaybackState(
                    PlaybackViewState.Ended,
                    "本集播放完毕。可以播放下一集。");
            }
        });

    private void OnMpvPlaybackFailed(
        object? sender,
        MpvPlaybackFailedEventArgs e)
        => _ = DispatcherQueue.TryEnqueue(
            () => HandleMpvPlaybackFailureAsync(e));

    private async void HandleMpvPlaybackFailureAsync(
        MpvPlaybackFailedEventArgs failure)
    {
        if (_isClosing || _session.Episode is not { } failedEpisode)
        {
            return;
        }

        _session.ClearResolvedMedia();
        await TryRecordRouteResultAsync(
            _session.AnimeContext.AnimeId,
            PlayerEpisodeGroup.GetRouteKey(failedEpisode),
            succeeded: false,
            TimeSpan.Zero);
        SetPlaybackState(
            PlaybackViewState.Failed,
            $"媒体播放失败：{failure.Message}。请重试或切换线路。");
        if (_experience.AutoFallbackEnabled
            && TrySelectNextRoute(failedEpisode))
        {
            await StartSelectedEpisodeAsync(allowFallback: false);
        }
    }

    private async Task LoadEpisodesAsync(CancellationToken cancellationToken)
    {
        _sourceLoadCancellation?.Cancel();
        _sourceLoadCancellation?.Dispose();
        _sourceLoadCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var activeCancellation = _sourceLoadCancellation;

        RenderAnimeContext();
        _loading.IsActive = true;
        _episodeList.ItemsSource = null;
        _playButton.IsEnabled = false;
        _sourceLookupInProgress = true;
        _status.Text =
            $"正在查找可用播放源…已加载 {_sourceCatalog.SourceCount} 个源，"
            + "每个源最多等待 15 秒。";
        try
        {
            var entries = await _sourceCatalog.GetEpisodesAsync(
                _session.AnimeContext,
                activeCancellation.Token);
            if (activeCancellation.IsCancellationRequested)
            {
                return;
            }

            var allEntries = entries.ToList();
            foreach (var request in _sourceCatalog.LastMappingRequests)
            {
                var selected = await PromptForMappingAsync(request);
                if (selected is not null)
                {
                    allEntries.AddRange(
                        await _sourceCatalog.SelectMappingAsync(
                            _session.AnimeContext,
                            selected,
                            activeCancellation.Token));
                }
            }

            _episodeGroups = PlayerEpisodeGroup.Create(
                allEntries,
                _experience.RouteHealth);
            _suppressSelectionPlayback = true;
            _episodeList.ItemsSource = _episodeGroups;
            _routeList.ItemsSource = null;
            if (_episodeGroups.Count > 0)
            {
                _episodeList.SelectedIndex = 0;
                var failures = _sourceCatalog.LastDiagnostics.Count;
                _status.Text = _mpv is null
                    ? _nativePlayerError
                    : failures == 0
                        ? $"已找到 {allEntries.Count} 个可播放项目。"
                        : $"已找到 {allEntries.Count} 个项目；{failures} 个源失败。";
            }
            else
            {
                _status.Text = _sourceCatalog.LastDiagnostics.Count == 0
                    ? "当前安装的源没有找到匹配剧集。"
                    : "没有找到剧集；请打开“源诊断”查看失败原因。";
            }
        }
        catch (OperationCanceledException)
            when (activeCancellation.IsCancellationRequested)
        {
        }
        finally
        {
            _suppressSelectionPlayback = false;
            if (ReferenceEquals(activeCancellation, _sourceLoadCancellation))
            {
                _sourceLookupInProgress = false;
                _loading.IsActive = false;
            }
        }
    }

    private async Task<SourceAnimeCandidate?> PromptForMappingAsync(
        SourceMappingRequest request)
    {
        var candidates = new ComboBox
        {
            ItemsSource = request.Candidates,
            SelectedIndex = 0,
            MinWidth = 420,
        };
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock
        {
            Text = $"{request.SourceName} 找到多个可能条目，请选择正确匹配：",
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(candidates);
        var dialog = new ContentDialog
        {
            XamlRoot = _root.XamlRoot,
            Title = "选择播放源映射",
            Content = content,
            PrimaryButtonText = "使用所选条目",
            CloseButtonText = "跳过此源",
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary
            ? candidates.SelectedItem as SourceAnimeCandidate
            : null;
    }

    private void OnManageSourcesClick(object sender, RoutedEventArgs e)
        => _openSourceManagement();

    internal async Task RefreshSourcesAsync()
    {
        if (_isClosing)
        {
            return;
        }

        try
        {
            await LoadEpisodesAsync(CancellationToken.None);
        }
#pragma warning disable CA1031 // Hot-reload failures are reported in the player.
        catch (Exception ex)
        {
            _status.Text = $"播放源已更新，但重新查询失败：{ex.Message}";
        }
#pragma warning restore CA1031
    }

    private void OnEpisodeSelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_episodeList.SelectedItem is not PlayerEpisodeGroup group)
        {
            lock (_activeContextSync)
            {
                _activeEpisodeNumber = null;
                _activePositionSeconds = null;
                _hasActivePlayback = false;
                _activeContextObservedAt = DateTimeOffset.UtcNow;
            }
            CancelPlaybackResolution();
            _routeList.ItemsSource = null;
            _playButton.IsEnabled = false;
            return;
        }

        CancelPlaybackResolution();
        lock (_activeContextSync)
        {
            _activeEpisodeNumber = group.TryGetEpisodeNumber(
                out var episodeNumber)
                ? episodeNumber
                : null;
            _activePositionSeconds = null;
            _hasActivePlayback = false;
            _activeContextObservedAt = DateTimeOffset.UtcNow;
        }
        _pendingResumePosition = null;
        ResetProgressReport();
        var routes = group.Routes.ToList();
        if (_experience.PreferredRouteByAnime.TryGetValue(
                _session.AnimeContext.AnimeId.ToString(),
                out var preferredRoute))
        {
            routes = routes
                .OrderByDescending(entry =>
                    string.Equals(
                        PlayerEpisodeGroup.GetRouteKey(entry),
                        preferredRoute,
                        StringComparison.Ordinal))
                .ToList();
        }

        var shouldStart = !_suppressSelectionPlayback && routes.Count > 0;
        var previousSuppression = _suppressSelectionPlayback;
        _suppressSelectionPlayback = true;
        _isChangingEpisode = true;
        _routeList.ItemsSource = routes;
        _routeList.SelectedIndex = routes.Count > 0 ? 0 : -1;
        _isChangingEpisode = false;
        _suppressSelectionPlayback = previousSuppression;
        _nextButton.IsEnabled =
            _episodeList.SelectedIndex >= 0
            && _episodeList.SelectedIndex < _episodeGroups.Count - 1;
        if (shouldStart)
        {
            _ = StartSelectedEpisodeAsync(
                allowFallback: _experience.AutoFallbackEnabled);
        }
    }

    private void OnRouteSelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_routeList.SelectedItem is not SourceEpisodeEntry entry)
        {
            CancelPlaybackResolution();
            _playButton.IsEnabled = false;
            return;
        }

        CancelPlaybackResolution();
        if (!_isChangingEpisode
            && _session.Media is not null
            && _mpv?.TryGetPlaybackState(
                out var position,
                out _,
                out _,
                out _) == true)
        {
            _pendingResumePosition = position;
        }

        if (_session.Media is not null)
        {
            TryRunPlayerCommand(
                () => _mpv?.Stop(),
                "正在切换播放项目…");
        }

        _session.SelectEpisode(entry);
        _playButton.IsEnabled = _mpv is not null;
        _status.Text = _mpv is null
            ? _nativePlayerError
            : entry.DisplayText;
        if (!_suppressSelectionPlayback)
        {
            _ = StartSelectedEpisodeAsync(
                allowFallback: _experience.AutoFallbackEnabled);
        }
    }

    private async void OnPlayClick(object sender, RoutedEventArgs e)
    {
        if (_mpv is null || _session.Episode is not { } selectedEpisode)
        {
            return;
        }

        if (_session.Media is not null)
        {
            TryRunPlayerCommand(
                _mpv.TogglePause,
                $"播放 / 暂停：{_session.Media.DisplayName}");
            return;
        }

        await StartSelectedEpisodeAsync(
            allowFallback: _experience.AutoFallbackEnabled);
    }

    private async Task StartSelectedEpisodeAsync(bool allowFallback)
    {
        if (_mpv is null || _session.Episode is not { } selectedEpisode)
        {
            return;
        }

        _playButton.IsEnabled = false;
        _loading.IsActive = true;
        lock (_activeContextSync)
        {
            _hasActivePlayback = false;
        }
        SetPlaybackState(
            PlaybackViewState.Resolving,
            $"正在解析：{selectedEpisode.DisplayText}");
        _playbackResolutionCancellation = new CancellationTokenSource();
        var activeCancellation = _playbackResolutionCancellation;
        var recovery = ResolutionTimeoutAction.None;
        var stopwatch = Stopwatch.StartNew();
        _resolutionStartedAtUtc = DateTimeOffset.UtcNow;
        try
        {
            var media = await _sourceCatalog.ResolveAsync(
                selectedEpisode.Episode,
                activeCancellation.Token);
            if (activeCancellation.IsCancellationRequested
                || _session.Episode != selectedEpisode)
            {
                return;
            }

            _session.SetResolvedMedia(media);
            _mpv.Load(media);
            _reachedEnd = false;
            _mpv.SetVolume(_volumeSlider.Value);
            _mpv.SetMuted(_isMuted);
            _mpv.SetSpeed(_playbackSpeed);
            SetPlaybackState(
                PlaybackViewState.Buffering,
                $"正在缓冲：{media.DisplayName}");
            _progressSlider.IsEnabled = true;
        }
        catch (OperationCanceledException)
            when (activeCancellation.IsCancellationRequested)
        {
        }
        catch (SourceResolutionException ex)
            when (ex.Kind == SourceResolutionFailureKind.Timeout)
        {
            if (!_isClosing)
            {
                SetPlaybackState(
                    PlaybackViewState.Failed,
                    "播放源解析超时。");
                recovery = await ShowResolutionTimeoutDialogAsync(ex);
            }
        }
#pragma warning disable CA1031 // A failed source should remain a recoverable player error.
        catch (Exception ex)
        {
            if (!_isClosing)
            {
                _session.ClearResolvedMedia();
                await TryRecordRouteResultAsync(
                    _session.AnimeContext.AnimeId,
                    PlayerEpisodeGroup.GetRouteKey(selectedEpisode),
                    succeeded: false,
                    stopwatch.Elapsed);
                SetPlaybackState(
                    PlaybackViewState.Failed,
                    $"播放失败：{ex.Message}");
                if (allowFallback && TrySelectNextRoute(selectedEpisode))
                {
                    _status.Text += " 正在尝试下一条线路…";
                    _ = DispatcherQueue.TryEnqueue(
                        () => _ = StartSelectedEpisodeAsync(
                            allowFallback: false));
                }
                else
                {
                    var action = await ShowPlaybackFailureDialogAsync(ex);
                    if (action == PlaybackFailureAction.NextRoute
                        && TrySelectNextRoute(selectedEpisode))
                    {
                        _ = DispatcherQueue.TryEnqueue(
                            () => _ = StartSelectedEpisodeAsync(
                                allowFallback: false));
                    }
                    else if (action == PlaybackFailureAction.Retry)
                    {
                        _ = DispatcherQueue.TryEnqueue(
                            () => _ = StartSelectedEpisodeAsync(
                                allowFallback: false));
                    }
                }
            }
        }
#pragma warning restore CA1031
        finally
        {
            if (ReferenceEquals(
                activeCancellation,
                _playbackResolutionCancellation))
            {
                activeCancellation.Dispose();
                _playbackResolutionCancellation = null;
                if (!_isClosing)
                {
                    if (_viewState != PlaybackViewState.Buffering)
                    {
                        _loading.IsActive = false;
                    }
                    _playButton.IsEnabled = _session.Episode is not null;
                }
            }
        }

        if (recovery is ResolutionTimeoutAction.Retry
            or ResolutionTimeoutAction.Verify
            && !_isClosing
            && _session.Episode == selectedEpisode)
        {
            if (recovery == ResolutionTimeoutAction.Verify
                && _lastTimedOutPageUri is { } pageUri)
            {
                _webResolver.RequireVerificationOnNextResolve(pageUri);
            }

            _ = DispatcherQueue.TryEnqueue(
                () => _ = StartSelectedEpisodeAsync(
                    allowFallback: recovery == ResolutionTimeoutAction.Retry));
        }
    }

    private Uri? _lastTimedOutPageUri;

    private async Task<ResolutionTimeoutAction> ShowResolutionTimeoutDialogAsync(
        SourceResolutionException exception)
    {
        _lastTimedOutPageUri = exception.PageUri;
        var dialog = new ContentDialog
        {
            XamlRoot = _root.XamlRoot,
            Title = "播放源解析超时",
            Content =
                "播放源解析超时。源站可能响应较慢、需要重新登录，"
                + "或者源规则已经失效。",
            PrimaryButtonText = "重新解析",
            SecondaryButtonText = "登录 / 验证",
            CloseButtonText = "切换播放源",
            DefaultButton = ContentDialogButton.Primary,
            IsSecondaryButtonEnabled = exception.PageUri is not null,
        };
        return await dialog.ShowAsync() switch
        {
            ContentDialogResult.Primary => ResolutionTimeoutAction.Retry,
            ContentDialogResult.Secondary => ResolutionTimeoutAction.Verify,
            _ => ResolutionTimeoutAction.None,
        };
    }

    private async Task<PlaybackFailureAction> ShowPlaybackFailureDialogAsync(
        Exception exception)
    {
        var hasNextRoute =
            _routeList.SelectedIndex >= 0
            && _routeList.SelectedIndex < _routeList.Items.Count - 1;
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock
        {
            Text =
                "当前线路未能开始播放。可以重试当前线路，"
                + "或者切换到下一条可用线路。",
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(new TextBlock
        {
            Text = exception.Message,
            Opacity = 0.65,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        });
        var dialog = new ContentDialog
        {
            XamlRoot = _root.XamlRoot,
            Title = "播放失败",
            Content = content,
            PrimaryButtonText = "重试",
            SecondaryButtonText = "下一线路",
            CloseButtonText = "取消",
            IsSecondaryButtonEnabled = hasNextRoute,
            DefaultButton = ContentDialogButton.Primary,
        };
        return await dialog.ShowAsync() switch
        {
            ContentDialogResult.Primary => PlaybackFailureAction.Retry,
            ContentDialogResult.Secondary =>
                PlaybackFailureAction.NextRoute,
            _ => PlaybackFailureAction.None,
        };
    }

    private void OnSeekBackward(object sender, RoutedEventArgs e)
        => TryRunPlayerCommand(
            () => _mpv?.SeekRelative(-10),
            "已后退 10 秒");

    private void OnSeekForward(object sender, RoutedEventArgs e)
        => TryRunPlayerCommand(
            () => _mpv?.SeekRelative(10),
            "已前进 10 秒");

    private void OnProgressPointerPressed(
        object sender,
        PointerRoutedEventArgs e)
        => _isProgressDragging = true;

    private void OnProgressPointerReleased(
        object sender,
        PointerRoutedEventArgs e)
    {
        if (!_isProgressDragging || _mpv is null)
        {
            return;
        }

        _isProgressDragging = false;
        TryRunPlayerCommand(
            () => _mpv.SeekAbsolute(_progressSlider.Value),
            $"已跳转至 {FormatTime(_progressSlider.Value)}");
    }

    private void OnProgressValueChanged(
        object sender,
        RangeBaseValueChangedEventArgs e)
    {
        if (_updatingControls || _mpv is null || !_progressSlider.IsEnabled)
        {
            return;
        }

        if (_isProgressDragging)
        {
            _timeText.Text =
                $"{FormatTime(e.NewValue)} / "
                + $"{FormatTime(_progressSlider.Maximum)}";
            return;
        }

        TryRunPlayerCommand(
            () => _mpv.SeekAbsolute(e.NewValue),
            $"已跳转至 {FormatTime(e.NewValue)}");
    }

    private void OnVolumeValueChanged(
        object sender,
        RangeBaseValueChangedEventArgs e)
    {
        if (_updatingControls || _mpv is null)
        {
            return;
        }

        TryRunPlayerCommand(
            () =>
            {
                _mpv.SetVolume(e.NewValue);
                if (e.NewValue > 0 && _isMuted)
                {
                    _isMuted = false;
                    _mpv.SetMuted(false);
                    _muteButton.Content = "音量";
                }
            },
            $"音量 {Math.Round(e.NewValue):0}%");
    }

    private void OnPlaybackTimerTick(object? sender, object e)
    {
        if (_mpv is null
            || !_mpv.TryGetPlaybackState(
                out var position,
                out var duration,
                out var volume,
                out var paused))
        {
            return;
        }

        _updatingControls = true;
        try
        {
            _lastPlaybackPosition = position;
            _lastPlaybackDuration = duration;
            lock (_activeContextSync)
            {
                _activePositionSeconds = position;
                _hasActivePlayback = duration > 0;
                _activeContextObservedAt = DateTimeOffset.UtcNow;
            }
            _progressSlider.Maximum = Math.Max(1, duration);
            _progressSlider.Value = Math.Clamp(position, 0, Math.Max(1, duration));
            _volumeSlider.Value = Math.Clamp(volume, 0, 100);
            _timeText.Text = $"{FormatTime(position)} / {FormatTime(duration)}";
            _playButton.Content = paused ? "播放" : "暂停";
            if (_pendingResumePosition is { } resumePosition
                && duration > 0)
            {
                _mpv.SeekAbsolute(Math.Min(resumePosition, duration));
                _pendingResumePosition = null;
            }

            if (_mpv.TryGetPlaybackFlags(out var muted, out var reachedEnd))
            {
                _isMuted = muted;
                _muteButton.Content = muted ? "取消静音" : "音量";
                if (reachedEnd && !_reachedEnd)
                {
                    _reachedEnd = true;
                    ReportProgressIfEligible(reachedNaturalEnd: true);
                    SetPlaybackState(
                        PlaybackViewState.Ended,
                        "本集播放完毕。可以播放下一集。");
                }
            }

            if (!_reachedEnd
                && duration > 0
                && _viewState is PlaybackViewState.Buffering
                    or PlaybackViewState.Playing
                    or PlaybackViewState.Paused)
            {
                SetPlaybackState(
                    paused
                        ? PlaybackViewState.Paused
                        : PlaybackViewState.Playing,
                    paused ? "已暂停" : GetNowPlayingText());
            }

            if (duration >= 300
                && position / duration >= 0.9)
            {
                ReportProgressIfEligible(reachedNaturalEnd: false);
            }
        }
        finally
        {
            _updatingControls = false;
        }
    }

    private void ResetProgressReport()
    {
        _lastPlaybackPosition = 0;
        _lastPlaybackDuration = 0;
        _progressReportedForEpisode = false;
    }

    private void ReportProgressIfEligible(bool reachedNaturalEnd)
    {
        if (_progressReportedForEpisode
            || _episodeList.SelectedItem is not PlayerEpisodeGroup group
            || !group.TryGetEpisodeNumber(out var episodeNumber)
            || _lastPlaybackDuration < 300)
        {
            return;
        }

        _progressReportedForEpisode = true;
        _ = ReportProgressAsync(new AnimePlaybackProgress(
            Guid.NewGuid().ToString("N"),
            _session.AnimeContext.AnimeId,
            episodeNumber,
            _lastPlaybackPosition,
            _lastPlaybackDuration,
            reachedNaturalEnd,
            DateTimeOffset.UtcNow));
    }

    private async Task ReportProgressAsync(AnimePlaybackProgress progress)
    {
        try
        {
            await _playbackProgressReporter.ReportAsync(progress);
        }
#pragma warning disable CA1031 // Progress reporting must never interrupt playback.
        catch (Exception)
        {
        }
#pragma warning restore CA1031
    }

    private void OnFullScreenClick(object sender, RoutedEventArgs e)
    {
        if (_appWindow is null)
        {
            return;
        }

        _isFullScreen = !_isFullScreen;
        _heading.Visibility =
            _isFullScreen ? Visibility.Collapsed : Visibility.Visible;
        _selectorBar.Visibility =
            _isFullScreen ? Visibility.Collapsed : Visibility.Visible;
        _status.Visibility =
            _isFullScreen ? Visibility.Collapsed : Visibility.Visible;
        _root.Padding =
            _isFullScreen ? new Thickness(0) : new Thickness(16);
        _root.RowSpacing = _isFullScreen ? 0 : 10;
        _appWindow.SetPresenter(
            _isFullScreen
                ? AppWindowPresenterKind.FullScreen
                : AppWindowPresenterKind.Overlapped);
        _fullScreenButton.Content =
            _isFullScreen ? "退出全屏" : "全屏";

        UpdateVideoBounds();
    }

    private void OnMuteClick(object sender, RoutedEventArgs e)
    {
        _isMuted = !_isMuted;
        TryRunPlayerCommand(
            () => _mpv?.SetMuted(_isMuted),
            _isMuted ? "已静音" : "已取消静音");
        _muteButton.Content = _isMuted ? "取消静音" : "音量";
        _ = PersistPreferencesSafelyAsync();
    }

    private void OnSpeedSelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_updatingControls
            || _speedList.SelectedItem is not string text
            || !double.TryParse(
                text.TrimEnd('×'),
                out var speed))
        {
            return;
        }

        _playbackSpeed = speed;
        TryRunPlayerCommand(
            () => _mpv?.SetSpeed(speed),
            $"播放速度 {speed:0.##}×");
        _ = PersistPreferencesSafelyAsync();
    }

    private void OnPreferenceControlReleased(
        object sender,
        PointerRoutedEventArgs e)
        => _ = PersistPreferencesSafelyAsync();

    private void OnNextEpisodeClick(object sender, RoutedEventArgs e)
    {
        if (_episodeList.SelectedIndex < 0
            || _episodeList.SelectedIndex >= _episodeGroups.Count - 1)
        {
            return;
        }

        _episodeList.SelectedIndex++;
    }

    private async void OnMoreClick(object sender, RoutedEventArgs e)
    {
        var autoFallback = new CheckBox
        {
            Content = "当前线路失败时自动尝试下一条线路",
            IsChecked = _experience.AutoFallbackEnabled,
        };
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock
        {
            Text = "播放源管理和诊断属于维护工具，不影响日常播放。",
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(autoFallback);
        var dialog = new ContentDialog
        {
            XamlRoot = _root.XamlRoot,
            Title = "播放器工具",
            Content = content,
            PrimaryButtonText = "源管理",
            SecondaryButtonText = "源诊断",
            CloseButtonText = "关闭",
        };
        var result = await dialog.ShowAsync();
        _experience.AutoFallbackEnabled = autoFallback.IsChecked == true;
        await PersistPreferencesAsync();
        if (result == ContentDialogResult.Primary)
        {
            OnManageSourcesClick(sender, e);
        }
        else if (result == ContentDialogResult.Secondary)
        {
            OnSourceDiagnosticsClick(sender, e);
        }
    }

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.OriginalSource is ComboBox
            or Slider
            or TextBox)
        {
            return;
        }

        switch (e.Key)
        {
            case VirtualKey.Space:
                OnPlayClick(_playButton, new RoutedEventArgs());
                e.Handled = true;
                break;
            case VirtualKey.Left:
                OnSeekBackward(sender, new RoutedEventArgs());
                e.Handled = true;
                break;
            case VirtualKey.Right:
                OnSeekForward(sender, new RoutedEventArgs());
                e.Handled = true;
                break;
            case VirtualKey.Up:
                _volumeSlider.Value =
                    Math.Min(100, _volumeSlider.Value + 5);
                e.Handled = true;
                break;
            case VirtualKey.Down:
                _volumeSlider.Value =
                    Math.Max(0, _volumeSlider.Value - 5);
                e.Handled = true;
                break;
            case VirtualKey.M:
                OnMuteClick(_muteButton, new RoutedEventArgs());
                e.Handled = true;
                break;
            case VirtualKey.F:
                OnFullScreenClick(_fullScreenButton, new RoutedEventArgs());
                e.Handled = true;
                break;
            case VirtualKey.Escape when _isFullScreen:
                OnFullScreenClick(_fullScreenButton, new RoutedEventArgs());
                e.Handled = true;
                break;
        }
    }

    private bool TrySelectNextRoute(SourceEpisodeEntry failed)
    {
        if (_routeList.ItemsSource is not IEnumerable<SourceEpisodeEntry> routes)
        {
            return false;
        }

        var routeList = routes.ToList();
        var index = routeList.IndexOf(failed);
        if (index < 0 || index >= routeList.Count - 1)
        {
            return false;
        }

        _suppressSelectionPlayback = true;
        _routeList.SelectedIndex = index + 1;
        _suppressSelectionPlayback = false;
        return _session.Episode is not null;
    }

    private void SetPlaybackState(
        PlaybackViewState state,
        string message)
    {
        _viewState = state;
        _status.Text = message;
        _loading.IsActive =
            state is PlaybackViewState.Resolving
                or PlaybackViewState.Buffering;
        _playButton.Content = state switch
        {
            PlaybackViewState.Playing => "暂停",
            PlaybackViewState.Paused or PlaybackViewState.Ended => "播放",
            PlaybackViewState.Resolving => "解析中",
            PlaybackViewState.Buffering => "缓冲中",
            PlaybackViewState.Failed => "重试",
            _ => "播放",
        };
    }

    private string GetNowPlayingText()
        => _session.Media is { } media
            ? $"正在播放：{media.DisplayName}"
            : _session.Episode is { } episode
                ? $"正在播放：{episode.DisplayText}"
                : "正在播放";

    private async Task LoadExperienceAsync()
    {
        _experience = await _experienceSettings.ReadAsync(
            CancellationToken.None);
        _updatingControls = true;
        try
        {
            _volumeSlider.Value = _experience.Volume;
            _isMuted = _experience.IsMuted;
            _muteButton.Content = _isMuted ? "取消静音" : "音量";
            _playbackSpeed = _experience.Speed;
            var speedText = $"{_playbackSpeed:0.##}×";
            var speedIndex = _speedList.Items
                .Cast<string>()
                .ToList()
                .FindIndex(item => item == speedText);
            _speedList.SelectedIndex = speedIndex >= 0 ? speedIndex : 2;
        }
        finally
        {
            _updatingControls = false;
        }

        _appWindow?.Resize(new SizeInt32(
            _experience.WindowWidth,
            _experience.WindowHeight));
    }

    private async Task TryRecordRouteResultAsync(
        int animeId,
        string routeKey,
        bool succeeded,
        TimeSpan latency)
    {
        try
        {
            await _experienceSettings.RecordRouteResultAsync(
                animeId,
                routeKey,
                succeeded,
                latency,
                CancellationToken.None);
            _experience = await _experienceSettings.ReadAsync(
                CancellationToken.None);
        }
#pragma warning disable CA1031 // Preference persistence cannot break playback.
        catch
        {
        }
#pragma warning restore CA1031
    }

    private async Task PersistPreferencesAsync()
    {
        var size = (!_isFullScreen ? _appWindow?.Size : null)
            ?? new SizeInt32(
                _experience.WindowWidth,
                _experience.WindowHeight);
        await _experienceSettings.UpdatePreferencesAsync(
            _volumeSlider.Value,
            _isMuted,
            _playbackSpeed,
            _experience.AutoFallbackEnabled,
            size.Width,
            size.Height,
            CancellationToken.None);
    }

    private async Task PersistPreferencesSafelyAsync()
    {
        try
        {
            await PersistPreferencesAsync();
        }
#pragma warning disable CA1031 // Preference persistence cannot break playback.
        catch
        {
        }
#pragma warning restore CA1031
    }

    private async void OnSourceDiagnosticsClick(
        object sender,
        RoutedEventArgs e)
    {
        var diagnostics = _sourceCatalog.LastDiagnostics;
        var message = _sourceLookupInProgress
            ? $"已加载 {_sourceCatalog.SourceCount} 个源，查找尚未完成。"
                + Environment.NewLine
                + "诊断会在本轮查找完成后更新。"
            : diagnostics.Count == 0
            ? $"已加载 {_sourceCatalog.SourceCount} 个源，最近一次查找没有源错误。"
            : string.Join(
                Environment.NewLine + Environment.NewLine,
                diagnostics.Select(item =>
                    $"{item.SourceName} ({item.SourceId})"
                    + Environment.NewLine
                    + $"{item.Operation}：{item.Message}"));
        var traceStatus = new TextBlock
        {
            Text = GetDiagnosticStatusText(),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.8,
        };
        var toggleCapture = new Button
        {
            Content = _diagnostics.IsEnabled ? "停止记录" : "开始记录",
        };
        toggleCapture.Click += async (_, _) =>
        {
            if (_diagnostics.IsEnabled)
            {
                await _diagnostics.StopAsync(CancellationToken.None);
            }
            else
            {
                await _diagnostics.StartAsync(CancellationToken.None);
            }

            toggleCapture.Content =
                _diagnostics.IsEnabled ? "停止记录" : "开始记录";
            traceStatus.Text = GetDiagnosticStatusText();
        };
        var export = new Button { Content = "导出诊断 ZIP" };
        export.Click += async (_, _) =>
        {
            try
            {
                var picker = new FileSavePicker
                {
                    SuggestedFileName =
                        $"AniMeido-playback-{DateTime.Now:yyyyMMdd-HHmmss}",
                };
                picker.FileTypeChoices.Add(
                    "ZIP 诊断包",
                    [".zip"]);
                InitializeWithWindow.Initialize(
                    picker,
                    WindowNative.GetWindowHandle(this));
                var file = await picker.PickSaveFileAsync();
                if (file is null)
                {
                    return;
                }

                await _diagnostics.ExportAsync(
                    file.Path,
                    CancellationToken.None);
                toggleCapture.Content = "开始记录";
                traceStatus.Text =
                    $"已导出：{file.Path}{Environment.NewLine}"
                    + "诊断记录已自动停止。";
            }
#pragma warning disable CA1031 // Export failures are shown in the dialog.
            catch (Exception ex)
            {
                traceStatus.Text = $"导出失败：{ex.Message}";
            }
#pragma warning restore CA1031
        };
        var clear = new Button { Content = "清除本地诊断" };
        var clearArmed = false;
        clear.Click += async (_, _) =>
        {
            if (!clearArmed)
            {
                clearArmed = true;
                clear.Content = "再次点击确认清除";
                traceStatus.Text =
                    "再次点击将删除 PlayerPlugin 的全部本地诊断记录。";
                return;
            }

            await _diagnostics.ClearAsync(CancellationToken.None);
            clearArmed = false;
            clear.Content = "清除本地诊断";
            toggleCapture.Content = "开始记录";
            traceStatus.Text = "本地播放诊断记录已清除。";
        };
        var controls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
        };
        controls.Children.Add(toggleCapture);
        controls.Children.Add(export);
        controls.Children.Add(clear);
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new ScrollViewer
        {
            MaxHeight = 320,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
            },
        });
        content.Children.Add(new TextBlock
        {
            Text = "播放诊断记录",
            FontSize = 18,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        content.Children.Add(new TextBlock
        {
            Text =
                "仅在明确开启后记录；Cookie、Authorization、密码、"
                + "Token 和 URL 查询参数会被脱敏。",
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(controls);
        content.Children.Add(traceStatus);
        var dialog = new ContentDialog
        {
            XamlRoot = _root.XamlRoot,
            Title = "播放源诊断",
            Content = content,
            CloseButtonText = "关闭",
        };
        await dialog.ShowAsync();
    }

    private string GetDiagnosticStatusText()
    {
        if (_diagnostics.IsEnabled)
        {
            return "诊断记录中。请关闭此窗口并复现一次播放失败。";
        }

        if (!string.IsNullOrWhiteSpace(_diagnostics.LastError))
        {
            return $"诊断写入错误：{_diagnostics.LastError}";
        }

        return _diagnostics.LastSessionDirectory is { } directory
            ? $"最近诊断：{directory}"
            : "当前没有播放诊断记录。";
    }

    private static string FormatTime(double seconds)
    {
        var duration = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return duration.TotalHours >= 1
            ? duration.ToString(@"h\:mm\:ss")
            : duration.ToString(@"mm\:ss");
    }

    private void CancelPlaybackResolution()
    {
        _playbackResolutionCancellation?.Cancel();
        _playbackResolutionCancellation?.Dispose();
        _playbackResolutionCancellation = null;
        _loading.IsActive = false;
    }

    private void TryRunPlayerCommand(Action action, string successMessage)
    {
        try
        {
            action();
            _status.Text = successMessage;
        }
#pragma warning disable CA1031 // Transport errors should remain in the player UI.
        catch (Exception ex)
        {
            _status.Text = $"播放控制失败：{ex.Message}";
        }
#pragma warning restore CA1031
    }

    private void OnVideoSurfaceSizeChanged(
        object sender,
        SizeChangedEventArgs e)
        => UpdateVideoBounds();

    private void UpdateVideoBounds()
        => _nativeVideoHost?.UpdateBounds(_videoSurface, _root);

    private void RenderAnimeContext()
    {
        _animeTitle.Text =
            $"{_session.AnimeContext.Title} · Bangumi #{_session.AnimeContext.AnimeId}";
        Title = $"{_session.AnimeContext.Title} - AniMeido 在线播放器";
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _isClosing = true;
        _ = PersistPreferencesSafelyAsync();
        _sourceLoadCancellation?.Cancel();
        _sourceLoadCancellation?.Dispose();
        _sourceLoadCancellation = null;
        CancelPlaybackResolution();
        _playbackTimer.Stop();
        _playbackTimer.Tick -= OnPlaybackTimerTick;
        if (_mpv is not null)
        {
            _mpv.FileLoaded -= OnMpvFileLoaded;
            _mpv.PlaybackEnded -= OnMpvPlaybackEnded;
            _mpv.PlaybackFailed -= OnMpvPlaybackFailed;
            _mpv.Dispose();
        }
        _mpv = null;
        _nativeVideoHost?.Dispose();
        _nativeVideoHost = null;
        _webResolver.CloseBrowserWindow();
        _root.KeyDown -= OnRootKeyDown;
        Activated -= OnWindowActivated;
        Closed -= OnWindowClosed;
    }

    private void ResizeWindow()
    {
        var windowHandle = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(windowHandle);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        _appWindow.Resize(new SizeInt32(1200, 780));
    }
}

internal enum ResolutionTimeoutAction
{
    None,
    Retry,
    Verify,
}

internal enum PlaybackFailureAction
{
    None,
    Retry,
    NextRoute,
}
