using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Contracts.Playback;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using AniMeido.Plugin.Base.Views.Controls;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Automation;
using Windows.System;
using System.Collections.Specialized;
using System.ComponentModel;

namespace AniMeido.Plugin.Base.Views;

public sealed partial class AnimeDetailPage : Page, INavigationAware, IDesktopLoadingPage
{
    private readonly IAnimeDataSource _dataSource;
    private readonly TrackingService _tracking;
    private readonly SavedTagService _savedTags;
    private readonly BrowseHistoryService _history;
    private readonly ArchiveService _archive;
    private readonly IPluginNavigator _navigator;
    private readonly IAnimePlaybackLauncher? _playback;
    private readonly ILogger<AnimeDetailPage> _logger;
    private readonly PersonalArchiveEditViewModel _editor;
    private readonly DesktopPageOperations _operations = new();
    private readonly SemaphoreSlim _trackingQueue = new(1, 1);
    private readonly SemaphoreSlim _tagQueue = new(1, 1);
    private readonly HashSet<string> _savedTagNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _savingTags = new(StringComparer.OrdinalIgnoreCase);
    private string[] _tagNames = [];
    private CancellationTokenSource? _visit;
    private CancellationTokenSource? _load;
    private ContentDialog? _dialog;
    private int _currentAnimeId;
    private int _navigationVersion;
    private bool _browseRecorded;
    private readonly PaperCardMotion _paperMotion;
    private PaperCardTrip? _paperTrip;
    private bool _paperEntryPending;
    private bool _paperOpening;
    /// <summary>卡片展开期间由纸面覆盖番剧库，窗口保留原页作背景。</summary>
    public bool IsPaperCardOpening => _paperOpening;
    /// <summary>纸面已铺满详情区域，可以释放下层页面；不等待资料读取。</summary>
    public event EventHandler? PaperCardExpanded;

    public DesktopPageLoadSurface LoadSurface { get; }
    public AnimeDetailViewModel ViewModel { get; }
    internal Task PendingActivation { get; private set; } = Task.CompletedTask;
    internal Task PendingOperation => _operations.Pending;

    public AnimeDetailPage(IAnimeDataSource dataSource, TrackingService trackingService, SavedTagService savedTagService,
        BrowseHistoryService browseHistory, IPluginNavigator pluginNavigator, ArchiveService archive,
        IEnumerable<IAnimePlaybackLauncher> playbackLaunchers, MetadataDataSource ratingSource, ILogger<AnimeDetailPage> logger)
    {
        _dataSource = dataSource;
        _tracking = trackingService;
        _savedTags = savedTagService;
        _history = browseHistory;
        _archive = archive;
        _navigator = pluginNavigator;
        _playback = playbackLaunchers.FirstOrDefault();
        _logger = logger;
        _editor = new(archive);
        ViewModel = new(dataSource, trackingService, ratingSource, logger);
        DataContext = ViewModel;
        InitializeComponent();
        _paperMotion = new(PaperMotionLayer, ContentScrollViewer);
        LoadSurface = new(ContentScrollViewer, LoadingOverlay, LoadingRing);
        InitializeDesktopArchiveSummary();
        ResetDesktopArchiveSummary();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public Task OnNavigatedToAsync(object? parameter)
    {
        var trip = parameter as PaperCardTrip;
        var id = trip?.Id ?? (parameter is int animeId ? animeId : 0);
        if (id == 0) return Task.CompletedTask;
        StopPaperMotion();
        _paperTrip = trip;
        _paperEntryPending = trip is not null && PaperCardTrip.Enabled;
        if (_visit is not null) AttachPaperMotion();
        if (_currentAnimeId != id) _browseRecorded = false;
        _currentAnimeId = id;
        _tagNames = [];
        _savedTagNames.Clear();
        TagContainer.Children.Clear();
        TagContainer.Visibility = Visibility.Collapsed;
        ResetDesktopArchiveSummary();
        _dialog?.Hide();
        ++_navigationVersion;
        CancelLoad();
        return _visit is null ? Task.CompletedTask : PendingActivation = LoadAsync(id, _navigationVersion, _visit.Token);
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_visit is not null) return;
        _visit = new();
        AttachPaperMotion();
        _browseRecorded = false;
        ViewModel.PropertyChanged += OnViewModelChanged;
        ViewModel.Characters.CollectionChanged += OnCharactersChanged;
        DesktopPresentationPreferences.AppearanceChanged += OnAppearanceChanged;
        if (_playback is not null) _playback.AvailabilityChanged += OnPlaybackChanged;
        UpdatePlayback();
        if (_currentAnimeId != 0) PendingActivation = LoadAsync(_currentAnimeId, ++_navigationVersion, _visit.Token);
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        if (_visit is not { } visit) return;
        _visit = null;
        StopPaperMotion();
        ++_navigationVersion;
        _dialog?.Hide();
        CancelLoad();
        CancelDesktopArchiveSummary();
        visit.Cancel();
        visit.Dispose();
        ViewModel.PropertyChanged -= OnViewModelChanged;
        ViewModel.Characters.CollectionChanged -= OnCharactersChanged;
        DesktopPresentationPreferences.AppearanceChanged -= OnAppearanceChanged;
        if (_playback is not null) _playback.AvailabilityChanged -= OnPlaybackChanged;
        DesktopTrackingMenu.Hide();
        foreach (var button in TagContainer.Children.OfType<Button>()) button.ContextFlyout?.Hide();
        ManagedImageLoader.Cancel(DetailCoverImage);
        LoadSurface.Unload();
        // 返回详情也会重新读取主资料，不让旧信号提前揭开尚未挂载的正文。
        LoadSurface.Reset();
    }

    private void CancelLoad()
    {
        ViewModel.LoadDetailCommand.Cancel();
        _load?.Cancel();
    }

    private async Task LoadAsync(int id, int version, CancellationToken visitToken)
    {
        using var loading = CancellationTokenSource.CreateLinkedTokenSource(visitToken);
        _load = loading;
        var token = loading.Token;
        LoadSurface.Reset();
        LoadingFailedImage.Visibility = Visibility.Collapsed;
        LoadingHint.Text = "加载中…";
        LoadSurface.SetPlaceholder(!_paperOpening);
        ContentScrollViewer.Visibility = _paperMotion.Active ? Visibility.Visible : Visibility.Collapsed;
        try
        {
            await _operations.DrainAsync();
            if (token.IsCancellationRequested || !IsCurrentNavigation(version, id)) return;
            await Task.WhenAll(ViewModel.LoadDetailCommand.ExecuteAsync(id), LoadTagsAsync(id, version, token),
                RefreshDesktopArchiveSummaryAsync(id, version, token));
            if (token.IsCancellationRequested || !IsCurrentNavigation(version, id)) return;
            UpdateState();
            if (ViewModel.HasData && ViewModel.AnimeDetail is { } anime && !_browseRecorded)
            {
                _browseRecorded = true;
                var record = RecordBrowseAsync(anime);
                _operations.Track(record);
                await record;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
#pragma warning disable CA1031 // 页面是本次组合读取的最终错误边界。
        catch (Exception error)
        {
            _logger.LogError(error, "作品详情读取失败 {AnimeId}。", id);
            if (IsCurrentNavigation(version, id)) Notify("作品资料暂时无法加载，请重试。");
        }
#pragma warning restore CA1031
        finally
        {
            if (ReferenceEquals(_load, loading)) _load = null;
            if (IsCurrentNavigation(version, id)) UpdateState();
        }
    }

    private async Task RecordBrowseAsync(Anime anime)
    {
        try { await _history.RecordAsync(anime.ID, anime.Title); }
#pragma warning disable CA1031 // 浏览记录失败保留作品详情，后台写入在此完成收尾。
        catch (Exception error) { _logger.LogWarning(error, "浏览记录未保存 {AnimeId}。", anime.ID); }
#pragma warning restore CA1031
    }

    private bool IsCurrentNavigation(int version, int id)
        => _visit is not null && version == _navigationVersion && id == _currentAnimeId;

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        // IsLoading 先于旧正文清空；同一作品重新加载时要等新的 HasData。
        if (args.PropertyName == nameof(AnimeDetailViewModel.HasData)
            || args.PropertyName == nameof(AnimeDetailViewModel.IsLoading) && !ViewModel.IsLoading
            || args.PropertyName == nameof(AnimeDetailViewModel.IsError) && ViewModel.IsError) UpdateState();
        if (args.PropertyName == nameof(AnimeDetailViewModel.StudiosText))
        {
            DetailStudio.Text = ViewModel.StudiosText ?? "";
            DetailStudio.Visibility = string.IsNullOrWhiteSpace(ViewModel.StudiosText) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private void UpdateState()
    {
        var current = ViewModel.HasData && ViewModel.AnimeDetail?.ID == _currentAnimeId;
        ContentScrollViewer.Visibility = current || _paperOpening || _paperMotion.Active && !ViewModel.IsError
            ? Visibility.Visible : Visibility.Collapsed;
        if (current) DetailTitle.Text = ViewModel.AnimeDetail!.Title;
        LoadingFailedImage.Visibility = ViewModel.IsError ? Visibility.Visible : Visibility.Collapsed;
        LoadingHint.Text = ViewModel.IsError ? (ViewModel.ErrorMessage ?? "作品资料暂时无法加载。") + "\n点击重试" : "加载中…";
        LoadSurface.SetPlaceholder(!_paperOpening && (!current || ViewModel.IsError), error: !_paperOpening && ViewModel.IsError);
        if (ViewModel.IsError && !_paperOpening)
        {
            StopPaperMotion();
            if (_paperTrip is { } trip) trip.Detail = null;
            LoadSurface.Present(LoadingMessage);
        }
        else if (current)
        {
            LoadSurface.Present();
            if (!_paperOpening && _paperMotion.Active)
                _paperMotion.RevealBody(DetailLayout.Children.Where(child => child != CoverSection && child != DetailHeader)
                    .Concat(DetailHeader.Children.Where(child => child != DetailTitle)));
        }
        PersonalArchiveButton.IsEnabled = current && !_editor.IsEditing;
        DesktopTrackingButton.IsEnabled = current && !ViewModel.IsLoading;
        if (current && ViewModel.AnimeDetail is { } anime && IsLoaded)
            ManagedImageLoader.ConfigureCover(DetailCoverImage, anime.ID, anime.CoverURL, 220);
        else ManagedImageLoader.Cancel(DetailCoverImage);
    }

    private void OnLoadingOverlayTapped(object sender, TappedRoutedEventArgs args)
    {
        if (_visit is not { } visit || !ViewModel.IsError) return;
        CancelLoad();
        PendingActivation = LoadAsync(_currentAnimeId, ++_navigationVersion, visit.Token);
    }
    public static Visibility BooleanToVisibility(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    private void OnPlaybackChanged(object? sender, EventArgs args)
    {
        var visit = _visit;
        DispatcherQueue.TryEnqueue(() => { if (visit is not null && ReferenceEquals(visit, _visit)) UpdatePlayback(); });
    }
    private void UpdatePlayback() => OnlinePlayButton.Visibility = _playback?.IsAvailable == true ? Visibility.Visible : Visibility.Collapsed;
    private void OnOnlinePlayClick(object sender, RoutedEventArgs args)
    {
        if (_playback?.IsAvailable == true && ViewModel.AnimeDetail is { } anime && _visit is { } visit)
            _operations.Track(OpenPlayerAsync(anime, _navigationVersion, visit.Token));
    }

    private async Task OpenPlayerAsync(Anime anime, int version, CancellationToken token)
    {
        try { await _playback!.LaunchAsync(new(anime.ID, anime.Title, anime.AlternateTitles), token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
#pragma warning disable CA1031 // 插件启动失败在播放入口反馈，离页后不再显示旧消息。
        catch (Exception error)
        {
            _logger.LogError(error, "未能打开在线播放器 {AnimeId}。", anime.ID);
            if (!token.IsCancellationRequested && IsCurrentNavigation(version, anime.ID)) Notify("播放器未能打开，请重试。");
        }
#pragma warning restore CA1031
    }

    private Task Write(Func<CancellationToken, Task> write)
    {
        if (_visit is not { } visit) return Task.CompletedTask;
        var operation = ExecuteWriteAsync(write, visit.Token, _currentAnimeId, _navigationVersion);
        _operations.Track(operation);
        return operation;
    }
    private async Task ExecuteWriteAsync(Func<CancellationToken, Task> write, CancellationToken token, int id, int version)
    {
        try { await write(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (ArgumentOutOfRangeException error)
        {
            if (!token.IsCancellationRequested && IsCurrentNavigation(version, id)) Notify(error.Message);
        }
#pragma warning disable CA1031 // 已接纳保存失败在这里记录，离页后省去旧提示。
        catch (Exception error)
        {
            _logger.LogError(error, "作品详情保存失败 {AnimeId}。", id);
            if (!token.IsCancellationRequested && IsCurrentNavigation(version, id)) Notify("保存未完成，请重试。");
        }
#pragma warning restore CA1031
    }
    private void Notify(string message)
    {
        if (DesktopFeedback.TryShow(message, error: true)) return;
        ErrorInfoBar.Message = message;
        ErrorInfoBar.IsOpen = true;
    }

    internal Task ChangeTrackingAsync(AnimeTrackingStatus status)
    {
        var id = _currentAnimeId;
        var version = _navigationVersion;
        if (id == 0 || ViewModel.IsLoading || !ViewModel.HasData || ViewModel.AnimeDetail?.ID != id) return Task.CompletedTask;
        return Write(async token =>
        {
            await _trackingQueue.WaitAsync();
            try
            {
                var previous = await _tracking.GetStatusAsync(id) ?? AnimeTrackingStatus.None;
                if (previous != status)
                {
                    if (status == AnimeTrackingStatus.None) await _tracking.RemoveStatusAsync(id);
                    else await _tracking.SetStatusAsync(id, status);
                }
                if (!token.IsCancellationRequested && IsCurrentNavigation(version, id)) ViewModel.CurrentStatus = status;
            }
            finally { _trackingQueue.Release(); }
        });
    }

    private async Task LoadTagsAsync(int id, int version, CancellationToken token)
    {
        try
        {
            var names = await _dataSource.GetTagsAsync(id, token);
            var saved = await _savedTags.GetAllSavedTagsAsync();
            if (token.IsCancellationRequested || !IsCurrentNavigation(version, id)) return;
            _tagNames = names.Select(tag => tag.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            _savedTagNames.Clear(); _savedTagNames.UnionWith(saved);
            RenderTags();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
#pragma warning disable CA1031 // 标签读取失败保留主详情。
        catch (Exception error)
        {
            if (!token.IsCancellationRequested && IsCurrentNavigation(version, id)) _logger.LogWarning(error, "作品标签暂时无法读取 {AnimeId}。", id);
        }
#pragma warning restore CA1031
    }

    private void RenderTags()
    {
        if (TagContainer.Children.OfType<Button>().Select(button => button.Tag as string).SequenceEqual(_tagNames))
        {
            UpdateTagStatuses();
            return;
        }
        TagContainer.Children.Clear();
        TagContainer.Visibility = _tagNames.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var name in _tagNames)
        {
            var button = new Button
            {
                Content = name, Tag = name, Margin = new(0, 0, 6, 4),
                Style = (Style)Application.Current.Resources["DesktopButtonStyle"],
                KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden,
            };
            AutomationProperties.SetName(button, name);
            UpdateTagStatus(button, name);
            button.Click += (_, _) => _navigator.Navigate(typeof(TagSearchResultPage), name);
            button.RightTapped += (_, args) => { args.Handled = true; _ = ToggleSavedTagAsync(name); };
            foreach (var (key, modifiers) in new[] { (VirtualKey.Application, VirtualKeyModifiers.None), (VirtualKey.F10, VirtualKeyModifiers.Shift) })
            {
                var shortcut = new KeyboardAccelerator { Key = key, Modifiers = modifiers, ScopeOwner = button };
                shortcut.Invoked += (accelerator, args) => { args.Handled = true; _ = ToggleSavedTagAsync(name); };
                button.KeyboardAccelerators.Add(shortcut);
            }
            TagContainer.Children.Add(button);
        }
    }

    private void UpdateTagStatuses()
    {
        foreach (var button in TagContainer.Children.OfType<Button>())
            if (button.Tag is string name) UpdateTagStatus(button, name);
    }

    private void UpdateTagStatus(Button button, string name)
    {
        var saved = _savedTagNames.Contains(name);
        var resources = Application.Current.Resources;
        button.Background = (Brush)resources[saved ? "DesktopSelectedBrush" : "DesktopButtonBrush"];
        button.BorderBrush = (Brush)resources[saved ? "DesktopAccentBrush" : "DesktopButtonEdgeBrush"];
        AutomationProperties.SetItemStatus(button, saved ? "已收藏" : "未收藏");
    }
    internal Task ToggleSavedTagAsync(string name)
    {
        if (_visit is null || !_savingTags.Add(name)) return Task.CompletedTask;
        var id = _currentAnimeId; var version = _navigationVersion;
        return Write(async token =>
        {
            await _tagQueue.WaitAsync();
            try
            {
                if (await _savedTags.IsTagSavedAsync(name)) await _savedTags.RemoveTagAsync(name);
                else await _savedTags.SaveTagAsync(name);
                var saved = await _savedTags.GetAllSavedTagsAsync();
                if (token.IsCancellationRequested || !IsCurrentNavigation(version, id)) return;
                _savedTagNames.Clear(); _savedTagNames.UnionWith(saved); UpdateTagStatuses();
            }
            finally { _tagQueue.Release(); _savingTags.Remove(name); }
        });
    }
    private void OnAppearanceChanged(object? sender, EventArgs args)
    {
        if (_paperTrip is { } trip)
        {
            StopPaperMotion();
            trip.Detail = null;
            _paperTrip = null;
            UpdateState();
        }
        UpdateTagStatuses();
    }

    private void AttachPaperMotion()
    {
        if (_paperTrip is not { } trip || !PaperCardTrip.Enabled) return;
        trip.Detail = null;
        LoadSurface.Ready += QueuePaperLayout;
        CoverSection.SizeChanged += OnPaperSizeChanged;
        DetailTitle.SizeChanged += OnPaperSizeChanged;
        ContentScrollViewer.SizeChanged += OnPaperSizeChanged;
        ContentScrollViewer.ViewChanged += OnPaperViewChanged;
        QueuePaperLayout(null, EventArgs.Empty);
        if (_paperEntryPending)
        {
            _paperOpening = true;
            DetailBackdrop.Opacity = 0;
            DetailTitle.Text = trip.Title;
            ReservePaperCoverSpace();
            _paperMotion.Hold(trip, trip.Origin, suspendInput: true);
            ContentScrollViewer.Visibility = Visibility.Visible;
        }
        _paperEntryPending = false;
    }

    private void OnPaperLayoutUpdated(object? sender, object args)
    {
        if (_paperTrip is not { } trip || (!_paperMotion.Active && !ReferenceEquals(LoadSurface.ReadyTarget, ContentScrollViewer))
            || ContentScrollViewer.ActualWidth <= 0 || DetailLayout.ActualHeight <= 0) return;
        if (!_paperOpening) ContentScrollViewer.LayoutUpdated -= OnPaperLayoutUpdated;
        var sheet = PaperCardPose.Bounds(ContentScrollViewer, PaperMotionLayer);
        var cover = PaperCardPose.Bounds(CoverSection, PaperMotionLayer);
        var title = PaperCardPose.Bounds(DetailTitle, PaperMotionLayer);
        // 首帧的封面可能还在解码。沿用卡片的宽高比，不让这次过渡依赖图片请求。
        if (cover.Height <= 0 && cover.Width > 0)
            cover.Height = Math.Min(CoverSection.MaxHeight, cover.Width * trip.Origin.Cover.Height / trip.Origin.Cover.Width);
        if (sheet.Height <= 0 || cover.Width <= 0 || cover.Height <= 0 || title.Width <= 0 || title.Height <= 0)
        {
            trip.Detail = null;
            _paperMotion.Stop();
            if (_paperOpening) OnPaperExpanded();
            return;
        }
        var pose = new PaperCardPose(sheet, cover, title, DetailTitle.FontSize,
            cover.Bottom > sheet.Top && cover.Top < sheet.Bottom, title.Bottom > sheet.Top && title.Top < sheet.Bottom);
        trip.Detail = pose;
        if (_paperMotion.Waiting) _paperMotion.Play(trip.Origin, pose, OnPaperExpanded);
        else _paperMotion.FollowDetail(pose, OnPaperExpanded);
    }

    private void OnPaperExpanded()
    {
        _paperOpening = false;
        DetailBackdrop.Opacity = 1;
        PaperCardExpanded?.Invoke(this, EventArgs.Empty);
        UpdateState();
    }

    private void ReservePaperCoverSpace()
    {
        if (_paperTrip is not { } trip || !double.IsNaN(CoverSection.Height)) return;
        var ratio = trip.Image is Microsoft.UI.Xaml.Media.Imaging.BitmapSource { PixelWidth: > 0, PixelHeight: > 0 } image
            ? (double)image.PixelHeight / image.PixelWidth : trip.Origin.Cover.Height / trip.Origin.Cover.Width;
        CoverSection.MinHeight = Math.Min(CoverSection.MaxHeight, CoverSection.Width * ratio);
    }

    private void QueuePaperLayout(object? sender, EventArgs args)
    {
        ContentScrollViewer.LayoutUpdated -= OnPaperLayoutUpdated;
        ContentScrollViewer.LayoutUpdated += OnPaperLayoutUpdated;
    }

    private void OnPaperSizeChanged(object sender, SizeChangedEventArgs args) => QueuePaperLayout(null, EventArgs.Empty);

    private void OnPaperViewChanged(object? sender, ScrollViewerViewChangedEventArgs args)
    {
        if (args.IsIntermediate) return;
        OnPaperLayoutUpdated(sender, args);
    }

    private void StopPaperMotion()
    {
        _paperEntryPending = false;
        _paperOpening = false;
        DetailBackdrop.Opacity = 1;
        LoadSurface.Ready -= QueuePaperLayout;
        CoverSection.SizeChanged -= OnPaperSizeChanged;
        DetailTitle.SizeChanged -= OnPaperSizeChanged;
        ContentScrollViewer.SizeChanged -= OnPaperSizeChanged;
        ContentScrollViewer.ViewChanged -= OnPaperViewChanged;
        ContentScrollViewer.LayoutUpdated -= OnPaperLayoutUpdated;
        _paperMotion.Stop();
        CoverSection.ClearValue(FrameworkElement.MinHeightProperty);
    }
    private void OnCharactersChanged(object? sender, NotifyCollectionChangedEventArgs args)
        => CharacterSection.Visibility = ViewModel.Characters.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public static string ActorLabel(IReadOnlyList<VoiceActor> actors) => actors.Count > 0 ? actors[0].Name : "配音资料待补充";
    private void OnCharacterClick(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: CharacterRole { Actors.Count: > 0 } role })
            _navigator.Navigate(typeof(PersonSearchResultPage), (role.Actors[0].VoiceActorId, role.Actors[0].Name));
    }
    private void OnAvatarLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is Image { Tag: string url } image) ManagedImageLoader.ConfigureAvatar(image, url, 64);
    }
    private void OnAvatarUnloaded(object sender, RoutedEventArgs args)
    {
        if (sender is Image image) ManagedImageLoader.Cancel(image);
    }
}
