using AniMeido.Contracts;
using AniMeido.Contracts.Desktop;
using AniMeido.Contracts.Playback;
using AniMeido.Plugin.Base.Exceptions;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using System.Globalization;
using System.Text.Json;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace AniMeido.Plugin.Base.Views;

public sealed partial class ArchivePage : Page, INavigationAware
{
    private readonly ArchiveService _archive;
    private readonly ArchiveReviewExporter _reviewExporter;
    private readonly IAnimePlaybackLauncher _playbackLauncher;
    private readonly IAnimeDataSource _animeDataSource;
    private readonly ScreenshotArchiveService _screenshots;
    private readonly ScreenshotShortcutAction _shortcut;
    private readonly IWindowHandleProvider _windowHandleProvider;
    private readonly ILogger<ArchivePage> _logger;
    private readonly ViewModels.PendingReviewViewModel _pendingReview;
    private readonly PersonalIdRemapService _idRemap;
    private readonly CollectionViewSource _screenshotGroups;
    private readonly ViewModels.ArchiveGalleryViewModel _gallery = new();
    private bool _suppressScreenshotSelectionChanged;
    private readonly ViewModels.ArchivePanelsViewModel _panels;
    private readonly ViewModels.ArchiveDetailsViewModel _details;
    private readonly ViewModels.ArchiveScreenshotSettingsViewModel _screenshotSettings;
    private ContentDialog? _screenshotSettingsDialog;
    private readonly ArchiveCoverRequests _coverRequests;
    private string? _requestedScreenshotId;
    private int? _requestedAnimeId;
    private CancellationTokenSource? _pageLifetime;
    private bool _suppressReviewYearChanged = true;
    private bool _navigationInitialized;
    private int _navigationVersion;
    private bool _isPlaybackAvailabilitySubscribed;

    public ArchivePage(
        ArchiveService archive,
        ArchiveQueryService queries,
        ArchiveReviewExporter reviewExporter,
        IAnimeDataSource animeDataSource,
        IAnimePlaybackLauncher playbackLauncher,
        ScreenshotArchiveService screenshots,
        ScreenshotShortcutAction shortcut,
        IWindowHandleProvider windowHandleProvider,
        ILogger<ArchivePage> logger,
        PendingReviewService pendingReview,
        PersonalIdRemapService idRemap)
    {
        _archive = archive;
        _screenshotSettings = new ViewModels.ArchiveScreenshotSettingsViewModel(archive);
        _panels = new ViewModels.ArchivePanelsViewModel(queries);
        _panels.PanelPublished += OnPanelPublished;
        _panels.PanelLoadCompleted += OnPanelLoadCompleted;
        _reviewExporter = reviewExporter;
        _animeDataSource = animeDataSource;
        _coverRequests = new ArchiveCoverRequests(animeDataSource.GetAnimeDetailAsync, logger);
        _playbackLauncher = playbackLauncher;
        _details = new ViewModels.ArchiveDetailsViewModel(queries, () => playbackLauncher.IsAvailable);
        _details.SelectionChanged += OnArchiveDetailsSelectionChanged;
        _details.TimelinePublished += ApplyTimelineFilter;
        _details.DetailsPublished += OnArchiveDetailsPublished;
        _details.LoadFailed += OnArchiveDetailsLoadFailed;
        _screenshots = screenshots;
        _shortcut = shortcut;
        _windowHandleProvider = windowHandleProvider;
        _logger = logger;
        _idRemap = idRemap;
        _pendingReview = new ViewModels.PendingReviewViewModel(pendingReview, idRemap, animeDataSource);
        InitializeComponent();
        _screenshotGroups = (CollectionViewSource)Resources["ScreenshotGroupsSource"];
        StatusFilter.SelectedIndex = 0;
        ArchiveTimelineFilter.SelectedIndex = 0;
        ApplyPlaybackAvailability();
        _playbackLauncher.AvailabilityChanged += OnPlaybackAvailabilityChanged;
        _isPlaybackAvailabilitySubscribed = true;
        ReviewYear.Value = DateTime.Now.Year;
        _suppressReviewYearChanged = false;
        ShowPanel("archives");
        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
    }

    private void OnArchiveCoverLoaded(object sender, RoutedEventArgs e)
        => _ = ConfigureArchiveCoverAsync(sender as Image);

    private void OnReviewCoverLoaded(object sender, RoutedEventArgs e)
        => _ = ConfigureArchiveCoverAsync(sender as Image, 200);

    private void OnReviewCoverDataContextChanged(
        FrameworkElement sender,
        DataContextChangedEventArgs args)
    {
        _ = args;
        _ = ConfigureArchiveCoverAsync(sender as Image, 200);
    }

    private void OnArchiveCoverDataContextChanged(
        FrameworkElement sender,
        DataContextChangedEventArgs args)
    {
        _ = args;
        _ = ConfigureArchiveCoverAsync(sender as Image);
    }

    internal static int? GetArchiveCoverAnimeId(object? context)
        => context switch
        {
            ArchiveListItem item => item.Archive.AnimeId,
            ArchiveReviewCover cover => cover.AnimeId,
            AnnualReviewMoment moment => moment.AnimeId,
            _ => null,
        };

    private async Task ConfigureArchiveCoverAsync(
        Image? image,
        int decodeWidth = 56)
    {
        var animeId = GetArchiveCoverAnimeId(image?.DataContext);
        if (image is null || animeId is null || _pageLifetime is null)
        {
            if (image is not null)
                ManagedImageLoader.Cancel(image);
            return;
        }

        var cancellationToken = _pageLifetime.Token;
        Task<AniMeido.Contracts.Models.Anime?>? request = null;
        bool IsCurrentRequest()
            => !cancellationToken.IsCancellationRequested
                && GetArchiveCoverAnimeId(image.DataContext) == animeId
                && _coverRequests.IsCurrent(animeId.Value, request);

        try
        {
            request = _coverRequests.GetOrStart(animeId.Value, cancellationToken);

            // 详情已取到时同步配置：同一封面不会被清空重载。
            // 还要等待时先清掉，复用的容器不留上一部作品的封面。
            if (!request.IsCompletedSuccessfully)
                ManagedImageLoader.Cancel(image);
            var anime = await request;
            if (IsCurrentRequest())
            {
                ManagedImageLoader.ConfigureCover(
                    image,
                    animeId.Value,
                    anime?.CoverURL,
                    decodeWidth);
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is HttpRequestException or MetadataServiceException
            or IOException or InvalidOperationException
            or TaskCanceledException or JsonException)
        {
            // 失败请求下次可重试，但不能清掉复用容器或替代请求的新封面。
            if (IsCurrentRequest())
                ManagedImageLoader.Cancel(image);
        }
    }

    private void OnScreenshotImageLoaded(object sender, RoutedEventArgs e)
        => ConfigureScreenshotImage(sender as Image);

    private void OnScreenshotImageDataContextChanged(
        FrameworkElement sender,
        DataContextChangedEventArgs args)
    {
        _ = args;
        ConfigureScreenshotImage(sender as Image);
    }

    private void ConfigureScreenshotImage(Image? image)
    {
        if (image?.DataContext is AnimeScreenshot screenshot)
            ManagedImageLoader.ConfigureLocal(
                image,
                screenshot.FilePath,
                ReferenceEquals(image, ScreenshotDetailImage) ? 600 : 350);
        else if (image is not null)
            ManagedImageLoader.Cancel(image);
    }

    public async Task OnNavigatedToAsync(object? parameter)
    {
        var requestedScreenshotId = parameter as string;
        var requestedAnimeId = parameter as int?;
        _requestedScreenshotId = requestedScreenshotId;
        _requestedAnimeId = requestedAnimeId;
        var navigationVersion = Interlocked.Increment(
            ref _navigationVersion);
        _navigationInitialized = true;
        EnsurePageLifetime();
        var lifetime = _pageLifetime;
        SubscribePendingChanges();
        await RefreshPendingAsync();
        if (!IsNavigationCurrent(navigationVersion, lifetime)) return;
        ShowPanel(requestedScreenshotId is null ? "archives" : "screenshots");
        await EnsureActivePanelAsync();
        if (!IsNavigationCurrent(navigationVersion, lifetime))
        {
            return;
        }

        if (requestedAnimeId is { } animeId)
        {
            ArchiveList.SelectedItem = ArchiveList.Items
                .OfType<ArchiveListItem>()
                .FirstOrDefault(item => item.Archive.AnimeId == animeId);
            if (ArchiveList.SelectedItem is not null)
            {
                ArchiveList.ScrollIntoView(ArchiveList.SelectedItem);
            }
        }
        if (requestedScreenshotId is not null)
        {
            ScreenshotList.SelectedItem = ScreenshotList.Items
                .OfType<AnimeScreenshot>()
                .FirstOrDefault(item =>
                    item.ScreenshotId == requestedScreenshotId);
            if (ScreenshotList.SelectedItem is not null)
            {
                ScreenshotList.ScrollIntoView(
                    ScreenshotList.SelectedItem);
            }
        }
    }

    private void EnsurePageLifetime()
    {
        if (_pageLifetime is null)
        {
            _pageLifetime = new CancellationTokenSource();
        }
        UpdatePendingPanelAvailability();
    }

    private async Task<bool> EnsureActivePanelAsync()
    {
        if (!_navigationInitialized || _pageLifetime is null)
            return false;

        var year = double.IsNaN(ReviewYear.Value)
            ? DateTime.Now.Year
            : (int)ReviewYear.Value;
        return await _panels.EnsureActiveAsync(year, _pageLifetime.Token);
    }

    private void OnPanelPublished(ArchivePanelKind panel)
    {
        switch (panel)
        {
            case ArchivePanelKind.Archives:
                ApplyArchiveFilter();
                break;
            case ArchivePanelKind.Statistics:
                RenderStatistics(_panels.Statistics!);
                break;
            case ArchivePanelKind.Review:
                var review = _panels.Review!;
                RenderReview(
                    review.Year,
                    review.Statistics,
                    review.Covers,
                    review.Moments,
                    review.Screenshots);
                break;
            case ArchivePanelKind.Screenshots:
                ApplyScreenshotFilter();
                break;
        }
    }

    private void OnPanelLoadCompleted(ArchivePanelKind panel, Exception? error)
    {
        if (error is null)
        {
            StatusInfoBar.IsOpen = false;
            return;
        }

        _logger.LogWarning(error, "Archive panel {Panel} failed to load", panel);
        ShowStatus(error.Message, InfoBarSeverity.Error);
    }

    private bool IsNavigationCurrent(
        int navigationVersion,
        CancellationTokenSource? lifetime)
        => navigationVersion == _navigationVersion
            && lifetime is not null
            && ReferenceEquals(_pageLifetime, lifetime)
            && !lifetime.IsCancellationRequested;

    private void Invalidate(params ArchivePanelKind[] panels)
        => _panels.Invalidate(panels);

    private async Task<bool> RefreshPanelsAsync(
        params ArchivePanelKind[] panels)
    {
        Invalidate(panels);
        if (panels.Contains(_panels.ActivePanel))
        {
            return await EnsureActivePanelAsync();
        }

        return true;
    }

    private async Task LoadAsync()
    {
        _panels.InvalidateAll();
        await EnsureActivePanelAsync();
    }

    private void ApplyScreenshotFilter()
    {
        if (_screenshotGroups is null || ScreenshotList is null)
            return;

        var selectedIds = ScreenshotList.SelectedItems
            .OfType<AnimeScreenshot>()
            .Select(item => item.ScreenshotId)
            .ToHashSet(StringComparer.Ordinal);
        var year = double.IsNaN(ScreenshotYearFilter.Value)
            ? null
            : (int?)ScreenshotYearFilter.Value;
        var restoredSelection = _gallery.ApplyFilter(
            _panels.Screenshots,
            _panels.ScreenshotTags,
            ScreenshotFilter.Text,
            year,
            selectedIds);

        _suppressScreenshotSelectionChanged = true;
        try
        {
            _screenshotGroups.Source = _gallery.VisibleScreenshots
                .GroupBy(item => item.CapturedAt.ToLocalTime().Date)
                .Select(group => new ScreenshotDayGroup(group.Key, group))
                .ToArray();
            if (_gallery.IsBatchMode)
            {
                foreach (var item in restoredSelection)
                {
                    if (!ScreenshotList.SelectedItems.Contains(item))
                        ScreenshotList.SelectedItems.Add(item);
                }
            }
            else
            {
                ScreenshotList.SelectedItem = restoredSelection.FirstOrDefault();
            }
        }
        finally
        {
            _suppressScreenshotSelectionChanged = false;
        }

        _gallery.CompleteFilterSelection(
            (ScreenshotList.SelectedItem as AnimeScreenshot)?.ScreenshotId);
        ScreenshotCountText.Text = $"截图 · {_gallery.VisibleScreenshots.Count} 张";
        ScreenshotEmptyState.Visibility = _gallery.VisibleScreenshots.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
        UpdateScreenshotDetails();
    }

    private void OnScreenshotSelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_screenshotGroups is null || _suppressScreenshotSelectionChanged)
            return;

        _gallery.UpdateSelection(
            e.AddedItems.OfType<AnimeScreenshot>().LastOrDefault()?.ScreenshotId,
            (ScreenshotList.SelectedItem as AnimeScreenshot)?.ScreenshotId);
        UpdateScreenshotDetails();
    }

    private void OnScreenshotBatchClick(object sender, RoutedEventArgs e)
    {
        _gallery.ToggleBatchMode();
        _suppressScreenshotSelectionChanged = true;
        try
        {
            ScreenshotList.SelectionMode = _gallery.IsBatchMode
                ? ListViewSelectionMode.Multiple : ListViewSelectionMode.Single;
            ScreenshotBatchButton.Content = _gallery.IsBatchMode
                ? "完成选择" : "批量选择";
            ScreenshotBatchActions.Visibility = _gallery.IsBatchMode
                ? Visibility.Visible : Visibility.Collapsed;
            ScreenshotDetailMoreButton.Visibility = _gallery.IsBatchMode
                ? Visibility.Collapsed : Visibility.Visible;
            if (!_gallery.IsBatchMode)
                ScreenshotList.SelectedItem = _gallery.DetailScreenshot;
        }
        finally
        {
            _suppressScreenshotSelectionChanged = false;
        }
        UpdateScreenshotDetails();
    }

    private AnimeScreenshot? GetDetailScreenshot()
        => _gallery.DetailScreenshot;

    private void UpdateScreenshotDetails()
    {
        var item = GetDetailScreenshot();
        ScreenshotDetailsEmptyState.Visibility = item is null
            ? Visibility.Visible : Visibility.Collapsed;
        ScreenshotDetailsPanel.Visibility = item is null
            ? Visibility.Collapsed : Visibility.Visible;
        ScreenshotSelectedCountText.Text = $"已选 {ScreenshotList.SelectedItems.Count} 张";
        ScreenshotBatchTagButton.IsEnabled = ScreenshotList.SelectedItems.Count > 0;
        ScreenshotBatchExportButton.IsEnabled = ScreenshotList.SelectedItems
            .OfType<AnimeScreenshot>().Any(selected => selected.FileExists);
        if (!ReferenceEquals(ScreenshotDetailImage.DataContext, item))
            ScreenshotDetailImage.DataContext = item;
        if (item is null)
            return;

        ScreenshotDetailAnime.Text = item.ReviewTitle;
        ScreenshotDetailTime.Text = item.CapturedAt.ToLocalTime()
            .ToString("yyyy/MM/dd HH:mm", CultureInfo.CurrentCulture);
        ScreenshotDetailEpisode.Text = item.EpisodeNumber is { } episode
            ? $"第 {episode} 集" : "未设置";
        ScreenshotDetailPosition.Text = item.PlaybackPositionSeconds is { } seconds
            ? TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss", CultureInfo.CurrentCulture)
            : "未记录";
        ScreenshotDetailSize.Text = $"{item.Width} × {item.Height}";
        var note = GetScreenshotNote(item);
        ScreenshotDetailContext.Text = string.IsNullOrWhiteSpace(note)
            ? "尚未添加备注" : note;
        ScreenshotDetailFileState.Text = item.FileExists
            ? string.Empty : "原图文件已缺失";
        ScreenshotDetailFileState.Visibility = item.FileExists
            ? Visibility.Collapsed : Visibility.Visible;
        ScreenshotOpenButton.IsEnabled = item.FileExists;
        ScreenshotExportButton.IsEnabled = item.FileExists;
        var tags = _panels.ScreenshotTags.GetValueOrDefault(item.ScreenshotId) ?? [];
        if (!ReferenceEquals(ScreenshotDetailTags.ItemsSource, tags))
            ScreenshotDetailTags.ItemsSource = tags;
    }

    private static string GetScreenshotNote(AnimeScreenshot item)
    {
        if (item.EpisodeNumber is not { } episode)
            return item.ContextNote;

        // Older captures repeated playback metadata in ContextNote. Keep the
        // note field for user text; playback metadata has its own fields.
        var generated = $"第 {episode} 集";
        if (item.PlaybackPositionSeconds is { } seconds)
        {
            var position = TimeSpan.FromSeconds(seconds);
            generated += " · " + (position.TotalHours >= 1
                ? position.ToString(@"h\:mm\:ss")
                : position.ToString(@"m\:ss"));
        }
        return string.Equals(item.ContextNote, generated, StringComparison.Ordinal)
            ? string.Empty : item.ContextNote;
    }

    private void OnScreenshotFilterChanged(
        object sender,
        TextChangedEventArgs e)
        => ApplyScreenshotFilter();

    private void OnScreenshotYearFilterChanged(
        NumberBox sender,
        NumberBoxValueChangedEventArgs args)
        => ApplyScreenshotFilter();

    private void RenderStatistics(ArchiveStatistics statistics)
    {
        StatisticsStartText.Text = statistics.RecordingStartedAt is { } started
            ? $"统计起点 {started.ToLocalTime():yyyy/MM/dd}"
            : "尚无记录";
        StatisticsArchiveCountText.Text = $"{statistics.ArchiveCount} 部";
        StatisticsRatedCountText.Text = $"{statistics.RatedCount} 部";
        StatisticsEntryCountText.Text = $"{statistics.EntryCount} 条";
        StatisticsScreenshotCountText.Text = $"{statistics.ScreenshotCount} 张";

        var tags = statistics.TagCounts
            .OrderByDescending(item => item.Value)
            .ThenBy(item => item.Key, StringComparer.CurrentCulture)
            .Take(6)
            .ToArray();
        var maximum = tags.Length == 0 ? 0 : tags[0].Value;
        StatisticsTagList.ItemsSource = tags.Select(item => new ArchiveTagBarData(
            item.Key,
            item.Value,
            maximum > 0 ? item.Value * 100d / maximum : 0)).ToArray();
        StatisticsTagsEmpty.Visibility = tags.Length == 0
            ? Visibility.Visible : Visibility.Collapsed;

        StatisticsCompletedText.Text = $"{statistics.CompletedEpisodeCount} 集";
        var minutes = Math.Max(0, statistics.EstimatedWatchMinutes);
        StatisticsDurationText.Text = $"{minutes / 60} 小时 {minutes % 60} 分钟";

        var coverage = statistics.ArchiveCount > 0
            ? Math.Clamp(statistics.RatedCount * 100d / statistics.ArchiveCount, 0, 100)
            : 0;
        StatisticsCoverageBar.Value = coverage;
        StatisticsCoveragePercentText.Text = $"{coverage:0}%";
        StatisticsCoverageText.Text = $"{statistics.RatedCount} / {statistics.ArchiveCount} 部";
        StatisticsUnratedText.Text = $"{Math.Max(0, statistics.ArchiveCount - statistics.RatedCount)} 部未评分";
        StatisticsStatusChangesText.Text = $"{statistics.TrackingChangeCount} 次";
    }

    private void RenderReview(
        int year,
        ArchiveStatistics statistics,
        IReadOnlyList<ArchiveReviewCover> covers,
        IReadOnlyList<AnnualReviewMoment> moments,
        IReadOnlyList<AnimeScreenshot> screenshots)
    {
        ReviewHeroYear.Text = year.ToString(CultureInfo.CurrentCulture);
        ReviewYearState.Text = year switch
        {
            var selected when selected < DateTime.Now.Year => "这一年已结束",
            var selected when selected > DateTime.Now.Year => "这一年尚未开始",
            _ => $"截至 {DateTime.Now:M月d日} · 本年度尚未结束",
        };
        ReviewRecordingStart.Text = statistics.RecordingStartedAt is { } started
            ? $"记录起点：{started.ToLocalTime():yyyy/MM/dd}"
            : "记录起点：暂无";
        ReviewArchiveCount.Text = $"{statistics.ArchiveCount} 部";
        ReviewEntryCount.Text = $"{statistics.EntryCount} 条";
        ReviewScreenshotCount.Text = $"{statistics.ScreenshotCount} 张";
        var minutes = Math.Max(0, statistics.EstimatedWatchMinutes);
        ReviewWatchDuration.Text = $"{minutes / 60} 小时 {minutes % 60} 分钟";

        ReviewCovers.ItemsSource = covers;
        ReviewCoversEmpty.Visibility = covers.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
        ReviewMoments.ItemsSource = moments;
        ReviewMomentsEmpty.Visibility = moments.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;

        var images = screenshots
            .Where(item => item.FileExists
                && item.CapturedAt.ToLocalTime().Year == year)
            .Take(2)
            .ToArray();
        ReviewScreenshots.ItemsSource = images;
        ReviewScreenshotsEmpty.Visibility = images.Length == 0
            ? Visibility.Visible : Visibility.Collapsed;
        ApplyPlaybackAvailability();
    }

    private void ApplyArchiveFilter()
    {
        var selectedAnimeId = _details.SelectedArchive?.Archive.AnimeId;
        var filter = ArchiveFilter.Text.Trim();
        var status = StatusFilter.SelectedIndex switch
        {
            1 => AniMeido.Contracts.Models.AnimeTrackingStatus.Watching,
            2 => AniMeido.Contracts.Models.AnimeTrackingStatus.PlanToWatch,
            3 => AniMeido.Contracts.Models.AnimeTrackingStatus.NotInterested,
            4 => AniMeido.Contracts.Models.AnimeTrackingStatus.Following,
            5 => AniMeido.Contracts.Models.AnimeTrackingStatus.Completed,
            6 => AniMeido.Contracts.Models.AnimeTrackingStatus.Dropped,
            _ => (AniMeido.Contracts.Models.AnimeTrackingStatus?)null,
        };
        double? minimumRating = double.IsNaN(MinimumRatingFilter.Value)
            ? null
            : MinimumRatingFilter.Value;
        var year = double.IsNaN(ArchiveYearFilter.Value)
            ? null
            : (int?)ArchiveYearFilter.Value;
        var filteredArchives = ViewModels.ArchiveBrowse.FilterArchives(
            _panels.Archives, filter, status, minimumRating, year);
        ArchiveList.ItemsSource = filteredArchives;
        ArchiveResultCount.Text = $"{filteredArchives.Count} 部";
        ArchiveListEmptyState.Visibility = filteredArchives.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        var restoredSelection = selectedAnimeId is { } animeId
            ? filteredArchives.FirstOrDefault(item =>
                item.Archive.AnimeId == animeId)
            : null;
        if (restoredSelection is not null)
        {
            ArchiveList.SelectedItem = restoredSelection;
        }
        else if (_details.SelectedArchive is not null)
        {
            _details.ClearSelection();
            ArchiveList.SelectedItem = null;
        }
        else if (filteredArchives.Count > 0)
        {
            ArchiveList.SelectedItem = filteredArchives[0];
        }
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
        => await LoadAsync();

    private void OnArchiveFilterChanged(
        object sender,
        TextChangedEventArgs e)
        => ApplyArchiveFilter();

    private void OnArchiveOptionChanged(
        object sender,
        SelectionChangedEventArgs e)
        => ApplyArchiveFilter();

    private void OnArchiveNumberFilterChanged(
        NumberBox sender,
        NumberBoxValueChangedEventArgs args)
        => ApplyArchiveFilter();

    private void OnClearArchiveFiltersClick(
        object sender,
        RoutedEventArgs e)
    {
        ArchiveFilter.Text = string.Empty;
        StatusFilter.SelectedIndex = 0;
        MinimumRatingFilter.Value = double.NaN;
        ArchiveYearFilter.Value = double.NaN;
        ApplyArchiveFilter();
    }

    private async void OnArchiveSelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
        => await ShowArchiveDetailsAsync(ArchiveList.SelectedItem as ArchiveListItem);

    private Task ShowArchiveDetailsAsync(ArchiveListItem? selectedArchive)
        => _details.SelectAsync(selectedArchive);

    private void OnArchiveDetailsSelectionChanged(ArchiveListItem? selectedArchive, int selectionVersion)
    {
        EntryList.SelectedItem = null;
        ApplyTimelineFilter();

        if (selectedArchive is null)
        {
            ArchiveSelectionEmptyState.Visibility = Visibility.Visible;
            ArchiveDetailPanel.Visibility = Visibility.Collapsed;
            EntryList.ItemsSource = null;
            ArchiveScreenshotStrip.ItemsSource = null;
            ArchiveScreenshotSection.Visibility = Visibility.Collapsed;
            ManagedImageLoader.Cancel(ArchiveHeroCover);
            UpdateEntryActions();
            return;
        }

        ArchiveSelectionEmptyState.Visibility = Visibility.Collapsed;
        ArchiveDetailPanel.Visibility = Visibility.Visible;
        ArchiveTitle.Text = selectedArchive.Archive.TitleSnapshot;
        ArchiveAnimeMetadata.Text = selectedArchive.Archive.AnimeId < 0 || selectedArchive.Unavailable ? "资料不可用" : "";
        ArchiveRatingText.Text = selectedArchive.RatingText;
        ArchiveStatusText.Text = selectedArchive.StatusText;
        ArchiveTagsText.Text = selectedArchive.Tags.Count == 0
            ? "尚无个人标签"
            : string.Join(" · ", selectedArchive.Tags);
        ArchiveMarkdownRenderer.Render(ArchiveNotePreview, selectedArchive.Archive.SummaryNote);
        ArchiveScreenshotStrip.ItemsSource = null;
        ArchiveScreenshotSection.Visibility = Visibility.Collapsed;
        ArchiveScreenshotCount.Text = string.Empty;
        ManagedImageLoader.Cancel(ArchiveHeroCover);
        if (_pageLifetime is { } lifetime)
        {
            _ = ConfigureHeroCoverAsync(selectedArchive.Archive.AnimeId, selectionVersion, lifetime.Token);
        }
    }

    private void OnArchiveDetailsPublished()
    {
        var screenshots = _details.DetailScreenshots;
        ArchiveScreenshotCount.Text = $"{screenshots.Count} 张";
        ArchiveScreenshotSection.Visibility = screenshots.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        ArchiveScreenshotStrip.ItemsSource = screenshots.Take(2).ToArray();
    }

    private void OnArchiveDetailsLoadFailed(Exception error)
    {
        _logger.LogWarning(error, "Archive details failed to load");
        ShowStatus($"档案详情加载失败：{error.Message}", InfoBarSeverity.Error);
    }

    private async Task ConfigureHeroCoverAsync(
        int animeId,
        int selectionVersion,
        CancellationToken cancellationToken)
    {
        try
        {
            var request = _coverRequests.GetOrStart(animeId, cancellationToken);
            var anime = await request;
            if (_details.IsCurrentSelection(selectionVersion)
                && !cancellationToken.IsCancellationRequested)
            {
                // 作品 ID 是内部标识（迁移后已不是 Bangumi 条目号，负数表示资料里没有），不显示给用户。
                ArchiveAnimeMetadata.Text = anime is null || animeId < 0
                    ? "资料不可用"
                    : string.Join(" · ", new[]
                    {
                        anime.AirDate?.Year.ToString(),
                        anime.Studio,
                    }.Where(value => !string.IsNullOrWhiteSpace(value)));
                ManagedImageLoader.ConfigureCover(
                    ArchiveHeroCover,
                    animeId,
                    anime?.CoverURL,
                    150);
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is HttpRequestException or MetadataServiceException
            or IOException or InvalidOperationException
            or TaskCanceledException or JsonException)
        {
            // 主视觉留空即可；失败的请求下次取用时会重新发起。
        }
    }

    private void OnTimelineFilterChanged(
        object sender,
        SelectionChangedEventArgs e)
        => ApplyTimelineFilter();

    private void ApplyTimelineFilter()
    {
        if (EntryList is null || ArchiveTimelineFilter is null)
            return;

        var kind = ArchiveTimelineFilter.SelectedIndex switch
        {
            1 => "感想",
            2 => "观看记录",
            3 => "状态变更",
            _ => null,
        };
        EntryList.ItemsSource = kind is null
            ? _details.TimelineItems
            : _details.TimelineItems.Where(item => item.Kind == kind).ToArray();
        UpdateEntryActions();
    }

    private Task RefreshSelectedTimelineAsync(int animeId)
        => _details.RefreshTimelineAsync(animeId);

    private async void OnEditArchiveClick(
        object sender,
        RoutedEventArgs e)
    {
        if (_details.SelectedArchive is null)
        {
            ShowStatus("请先选择一部番剧。", InfoBarSeverity.Warning);
            return;
        }

        var archive = _details.SelectedArchive;
        var rating = new NumberBox
        {
            Header = "个人评分",
            Minimum = 0.5,
            Maximum = 10,
            SmallChange = 0.5,
            Value = archive.Archive.PersonalRating ?? double.NaN,
        };
        var tags = new TextBox
        {
            Header = "个人标签（逗号分隔）",
            Text = string.Join(", ", archive.Tags),
        };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(rating);
        panel.Children.Add(tags);
        var dialog = CreateDialog("编辑档案", panel, "保存");
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            var navigationVersion = _navigationVersion;
            var lifetime = _pageLifetime;
            try
            {
                try
                {
                    await _archive.SaveArchiveWithTagsAsync(
                        archive.Archive.AnimeId,
                        archive.Archive.TitleSnapshot,
                        double.IsNaN(rating.Value) ? null : rating.Value,
                        archive.Archive.SummaryNote,
                        SplitTags(tags.Text));
                }
                catch (ArgumentException)
                {
                    args.Cancel = true;
                    if (IsNavigationCurrent(navigationVersion, lifetime))
                        ShowStatus("评分应为0.5到10之间的半分，例如8或8.5，也可以留空。", InfoBarSeverity.Warning);
                    return;
                }
                catch (PersonalDataBusyException ex)
                {
                    args.Cancel = true;
                    if (IsNavigationCurrent(navigationVersion, lifetime))
                        ShowStatus(ex.Message, InfoBarSeverity.Warning);
                    return;
                }
                catch (Exception ex) when (ex is SqliteException
                    or IOException or InvalidOperationException)
                {
                    args.Cancel = true;
                    _logger.LogError(ex, "Archive edit failed to save for {AnimeId}", archive.Archive.AnimeId);
                    if (IsNavigationCurrent(navigationVersion, lifetime))
                        ShowStatus("档案未保存，请稍后重试。", InfoBarSeverity.Error);
                    return;
                }

                if (!IsNavigationCurrent(navigationVersion, lifetime))
                {
                    Invalidate(ArchivePanelKind.Archives, ArchivePanelKind.Statistics, ArchivePanelKind.Review);
                    return;
                }

                try
                {
                    var refreshed = await RefreshPanelsAsync(
                        ArchivePanelKind.Archives,
                        ArchivePanelKind.Statistics,
                        ArchivePanelKind.Review);
                    if (IsNavigationCurrent(navigationVersion, lifetime))
                    {
                        ShowStatus(
                            refreshed ? "档案已保存。" : "档案已保存，但列表未能刷新。请点刷新查看。",
                            refreshed ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
                    }
                }
                catch (Exception ex) when (ex is SqliteException
                    or IOException or InvalidOperationException
                    or ArgumentException)
                {
                    _logger.LogWarning(ex, "Archive was saved but its panels failed to refresh for {AnimeId}",
                        archive.Archive.AnimeId);
                    if (IsNavigationCurrent(navigationVersion, lifetime))
                        ShowStatus("档案已保存，但列表未能刷新。请点刷新查看。", InfoBarSeverity.Warning);
                }
            }
            finally
            {
                deferral.Complete();
            }
        };
        await dialog.ShowAsync();
    }

    private async void OnEditNoteClick(object sender, RoutedEventArgs e)
    {
        if (_details.SelectedArchive is not { } selected)
            return;

        var availableWidth = Math.Max(420, XamlRoot.Size.Width - 96);
        var editorWidth = Math.Min(1080, availableWidth);
        var sideBySide = editorWidth >= 760;
        var editorHeight = Math.Clamp(XamlRoot.Size.Height - 250, 260, 560);
        if (!sideBySide)
            editorHeight = Math.Min(editorHeight / 2, 300);

        var source = new TextBox
        {
            Text = selected.Archive.SummaryNote,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = editorHeight,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Code"),
            FontSize = 15,
            Padding = new Thickness(16),
        };
        var preview = new RichTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Microsoft YaHei UI"),
            FontSize = 16,
            LineHeight = 27,
        };
        ArchiveMarkdownRenderer.Render(preview, source.Text);
        // 停止输入片刻后再重绘预览，长笔记逐键整篇重绘会拖慢输入。
        var previewTimer = DispatcherQueue.CreateTimer();
        previewTimer.Interval = TimeSpan.FromMilliseconds(150);
        previewTimer.IsRepeating = false;
        previewTimer.Tick += (_, _) =>
            ArchiveMarkdownRenderer.Render(preview, source.Text);
        source.TextChanged += (_, _) =>
        {
            previewTimer.Stop();
            previewTimer.Start();
        };
        var editorColumn = new StackPanel { Spacing = 10 };
        editorColumn.Children.Add(new TextBlock
        {
            Text = "MARKDOWN",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        editorColumn.Children.Add(source);
        var previewColumn = new StackPanel { Spacing = 10 };
        previewColumn.Children.Add(new TextBlock
        {
            Text = "实时预览",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        previewColumn.Children.Add(new ScrollViewer
        {
            Content = preview,
            Height = editorHeight,
            Padding = new Thickness(16),
        });
        var layout = new Grid { ColumnSpacing = 18, RowSpacing = 16 };
        if (sideBySide)
        {
            layout.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star),
            });
            layout.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star),
            });
            Grid.SetColumn(previewColumn, 1);
        }
        else
        {
            layout.RowDefinitions.Add(new RowDefinition
            {
                Height = GridLength.Auto,
            });
            layout.RowDefinitions.Add(new RowDefinition
            {
                Height = GridLength.Auto,
            });
            Grid.SetRow(previewColumn, 1);
        }
        layout.Children.Add(editorColumn);
        layout.Children.Add(previewColumn);
        var dialog = CreateDialog("编辑作品笔记", layout, "保存");
        dialog.Resources["ContentDialogMaxWidth"] = editorWidth + 64;
        dialog.Resources["ContentDialogMinWidth"] = editorWidth + 64;
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                await _archive.UpsertArchiveAsync(
                    selected.Archive.AnimeId,
                    selected.Archive.TitleSnapshot,
                    selected.Archive.PersonalRating,
                    source.Text);
                if (_details.SelectedArchive?.Archive.AnimeId == selected.Archive.AnimeId)
                {
                    ArchiveMarkdownRenderer.Render(ArchiveNotePreview, source.Text);
                }
                await RefreshPanelsAsync(
                    ArchivePanelKind.Archives,
                    ArchivePanelKind.Statistics,
                    ArchivePanelKind.Review);
                ShowStatus("作品笔记已保存。", InfoBarSeverity.Success);
            }
            catch (Exception ex) when (ex is SqliteException
                or IOException or InvalidOperationException
                or ArgumentException)
            {
                args.Cancel = true;
                _logger.LogWarning(ex, "Archive note failed to save");
                ShowStatus($"笔记保存失败：{ex.Message}", InfoBarSeverity.Error);
            }
            finally
            {
                deferral.Complete();
            }
        };
        await dialog.ShowAsync();
        previewTimer.Stop();
    }

    private async void OnAddEntryClick(object sender, RoutedEventArgs e)
    {
        if (_details.SelectedArchive is not { } selected)
        {
            ShowStatus("请先选择一部番剧。", InfoBarSeverity.Warning);
            return;
        }

        var body = new TextBox
        {
            Header = "观看感想",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 120,
        };
        var episode = new NumberBox
        {
            Header = "集数（可选）",
            Minimum = 1,
            SpinButtonPlacementMode =
                NumberBoxSpinButtonPlacementMode.Compact,
        };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(episode);
        panel.Children.Add(body);
        var dialog = CreateDialog("追加观看感想", panel, "保存");
        if (await dialog.ShowAsync() == ContentDialogResult.Primary
            && !string.IsNullOrWhiteSpace(body.Text))
        {
            await _archive.AddEntryAsync(
                selected.Archive.AnimeId,
                DateTimeOffset.Now,
                double.IsNaN(episode.Value) ? null : (int)episode.Value,
                body.Text);
            await RefreshSelectedTimelineAsync(selected.Archive.AnimeId);
            await RefreshPanelsAsync(
                ArchivePanelKind.Archives,
                ArchivePanelKind.Statistics,
                ArchivePanelKind.Review);
        }
    }

    private async void OnEditEntryClick(
        object sender,
        RoutedEventArgs e)
    {
        if (EntryList.SelectedItem is not ArchiveTimelineItem
            { Entry: { } entry })
        {
            ShowStatus("请先选择一条感想。", InfoBarSeverity.Warning);
            return;
        }

        var body = new TextBox
        {
            Header = "观看感想",
            Text = entry.Body,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 120,
        };
        var episode = new NumberBox
        {
            Header = "集数（可选）",
            Minimum = 1,
            Value = entry.EpisodeNumber ?? double.NaN,
        };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(episode);
        panel.Children.Add(body);
        var dialog = CreateDialog("编辑观看感想", panel, "保存");
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await _archive.UpdateEntryAsync(
                entry.EntryId,
                entry.OccurredAt,
                double.IsNaN(episode.Value)
                    ? null
                    : (int)episode.Value,
                body.Text);
            await RefreshSelectedTimelineAsync(entry.AnimeId);
            await RefreshPanelsAsync(
                ArchivePanelKind.Archives,
                ArchivePanelKind.Statistics,
                ArchivePanelKind.Review);
        }
    }

    private async void OnDeleteEntryClick(
        object sender,
        RoutedEventArgs e)
    {
        if (EntryList.SelectedItem is not ArchiveTimelineItem
            { Entry: { } entry })
        {
            ShowStatus("请先选择一条感想。", InfoBarSeverity.Warning);
            return;
        }

        var dialog = CreateDialog(
            "删除观看感想",
            "此操作无法撤销。",
            "删除");
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await _archive.DeleteEntryAsync(entry.EntryId);
            await RefreshSelectedTimelineAsync(entry.AnimeId);
            await RefreshPanelsAsync(
                ArchivePanelKind.Archives,
                ArchivePanelKind.Statistics,
                ArchivePanelKind.Review);
        }
    }

    private async void OnAddManualEventClick(
        object sender,
        RoutedEventArgs e)
    {
        if (!_playbackLauncher.IsAvailable)
            return;

        if (_details.SelectedArchive is not { } selected)
        {
            ShowStatus("请先选择一部番剧。", InfoBarSeverity.Warning);
            return;
        }

        var date = new CalendarDatePicker
        {
            Header = "观看日期",
            Date = DateTimeOffset.Now,
        };
        var time = new TimePicker
        {
            Header = "观看时间",
            Time = DateTimeOffset.Now.TimeOfDay,
        };
        var from = new NumberBox
        {
            Header = "起始集",
            Minimum = 1,
            Value = 1,
        };
        var to = new NumberBox
        {
            Header = "结束集",
            Minimum = 1,
            Value = 1,
        };
        var minutes = new NumberBox
        {
            Header = "观看分钟数（可选）",
            Minimum = 1,
        };
        var note = new TextBox { Header = "备注" };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(date);
        panel.Children.Add(time);
        panel.Children.Add(from);
        panel.Children.Add(to);
        panel.Children.Add(minutes);
        panel.Children.Add(note);
        var dialog = CreateDialog("补录观看事件", panel, "保存");
        if (await dialog.ShowAsync() != ContentDialogResult.Primary
            || date.Date is null)
        {
            return;
        }

        try
        {
            var localDateTime = date.Date.Value.Date.Add(time.Time);
            var occurredAt = new DateTimeOffset(
                localDateTime,
                TimeZoneInfo.Local.GetUtcOffset(localDateTime));
            await _archive.AddManualWatchEventAsync(new ManualWatchEvent(
                Guid.NewGuid().ToString("N"),
                selected.Archive.AnimeId,
                selected.Archive.TitleSnapshot,
                occurredAt,
                (int)from.Value,
                (int)to.Value,
                double.IsNaN(minutes.Value)
                    ? null
                    : (int)minutes.Value,
                note.Text,
                DateTimeOffset.UtcNow));
            await RefreshPanelsAsync(
                ArchivePanelKind.Statistics,
                ArchivePanelKind.Review);
            await RefreshSelectedTimelineAsync(selected.Archive.AnimeId);
            ShowStatus("观看事件已补录。",
                InfoBarSeverity.Success);
        }
        catch (ArgumentException ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Warning);
        }
    }

    private void OnEntrySelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
        => UpdateEntryActions();

    private void UpdateEntryActions()
    {
        var hasSelection = EntryList.SelectedItem is ArchiveTimelineItem
            { Entry: not null };
        EditEntryButton.IsEnabled = hasSelection;
        DeleteEntryButton.IsEnabled = hasSelection;
    }

    private async void OnSectionButtonClick(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag })
        {
            ShowPanel(tag);
            await EnsureActivePanelAsync();
        }
    }

    private void ShowPanel(string tag)
    {
        var nextPanel = tag switch
        {
            "statistics" => ArchivePanelKind.Statistics,
            "review" => ArchivePanelKind.Review,
            "screenshots" => ArchivePanelKind.Screenshots,
            _ => ArchivePanelKind.Archives,
        };
        _panels.SelectPanel(nextPanel);
        ArchivesPanel.Visibility =
            tag == "archives" ? Visibility.Visible : Visibility.Collapsed;
        StatisticsPanel.Visibility =
            tag == "statistics" ? Visibility.Visible : Visibility.Collapsed;
        ReviewPanel.Visibility =
            tag == "review" ? Visibility.Visible : Visibility.Collapsed;
        ScreenshotsPanel.Visibility =
            tag == "screenshots" ? Visibility.Visible : Visibility.Collapsed;
        ArchivesSectionButton.IsChecked = tag == "archives";
        StatisticsSectionButton.IsChecked = tag == "statistics";
        ReviewSectionButton.IsChecked = tag == "review";
        ScreenshotsSectionButton.IsChecked = tag == "screenshots";
    }

    private void OnShowScreenshotMoreActionsClick(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            Microsoft.UI.Xaml.Controls.Primitives.FlyoutBase
                .ShowAttachedFlyout(element);
        }
    }

    private async void OnReviewYearChanged(
        NumberBox sender,
        NumberBoxValueChangedEventArgs args)
    {
        _ = sender;
        _ = args;
        if (_suppressReviewYearChanged)
        {
            return;
        }

        if (double.IsNaN(ReviewYear.Value))
            return;

        await RefreshPanelsAsync(ArchivePanelKind.Review);
    }

    private void OnReviewPreviousYearClick(object sender, RoutedEventArgs e)
    {
        var year = double.IsNaN(ReviewYear.Value)
            ? DateTime.Now.Year : (int)ReviewYear.Value;
        ReviewYear.Value = Math.Max(2000, year - 1);
    }

    private void OnReviewNextYearClick(object sender, RoutedEventArgs e)
    {
        var year = double.IsNaN(ReviewYear.Value)
            ? DateTime.Now.Year : (int)ReviewYear.Value;
        ReviewYear.Value = Math.Min(2100, year + 1);
    }

    private async void OnReviewMoreScreenshotsClick(
        object sender,
        RoutedEventArgs e)
    {
        ScreenshotYearFilter.Value = ReviewYear.Value;
        ShowPanel("screenshots");
        await EnsureActivePanelAsync();
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (!_navigationInitialized)
        {
            return;
        }

        EnsurePageLifetime();
        SubscribePendingChanges();
        _ = RefreshPendingAsync();
        if (!_isPlaybackAvailabilitySubscribed)
        {
            _playbackLauncher.AvailabilityChanged += OnPlaybackAvailabilityChanged;
            _isPlaybackAvailabilitySubscribed = true;
        }
        ApplyPlaybackAvailability();
        // 离开时取消了选中作品的时间线、截图与封面请求；返回后重新读取，
        // 之后追加或修改感想时时间线也能照常刷新。
        if (_details.SelectedArchive is { } selected && !_details.HasActiveSelection)
        {
            _ = ShowArchiveDetailsAsync(selected);
        }
        _ = EnsureActivePanelAsync();
    }

    private async void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        UnsubscribePendingChanges();
        // 返回导航会复用本页；先使旧请求和订阅失效，再等待它们完成。
        Interlocked.Increment(ref _navigationVersion);
        var stoppingDetails = _details.CancelInflightAsync();
        HideScreenshotSettingsDialog();
        _pageLifetime?.Cancel();
        _pageLifetime?.Dispose();
        _pageLifetime = null;
        UpdatePendingPanelAvailability();
        _panels.CancelInflight();
        if (_isPlaybackAvailabilitySubscribed)
        {
            _playbackLauncher.AvailabilityChanged -= OnPlaybackAvailabilityChanged;
            _isPlaybackAvailabilitySubscribed = false;
        }
        // 已取到的详情只是数据，返回页面时继续复用；未完成或失败的请求随页面一起作废。
        _coverRequests.DiscardIncomplete();
        ManagedImageLoader.Cancel(ArchiveHeroCover);

        try
        {
            await stoppingDetails;
        }
#pragma warning disable CA1031 // 离页事件最终报告请求停止失败，不让旧续体修改重新激活的页面。
        catch (Exception error)
        {
            _logger.LogWarning(error, "Archive detail request shutdown failed");
        }
#pragma warning restore CA1031
    }

    private void OnPlaybackAvailabilityChanged(object? sender, EventArgs e)
        => DispatcherQueue.TryEnqueue(() =>
        {
            ApplyPlaybackAvailability();
            if (_details.SelectedArchive is { } selected)
                _ = RefreshSelectedTimelineAsync(selected.Archive.AnimeId);
        });

    private void ApplyPlaybackAvailability()
    {
        var available = _playbackLauncher.IsAvailable;
        var playbackVisibility = available
            ? Visibility.Visible : Visibility.Collapsed;
        ScreenshotDetailEpisodeLabel.Visibility = playbackVisibility;
        ScreenshotDetailEpisode.Visibility = playbackVisibility;
        ScreenshotDetailPositionLabel.Visibility = playbackVisibility;
        ScreenshotDetailPosition.Visibility = playbackVisibility;
        StatisticsWatchCard.Visibility = available
            ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumnSpan(StatisticsTagsCard, available ? 1 : 2);
        ReviewScreenshotsCard.Visibility = available
            ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumnSpan(ReviewMomentsCard, available ? 1 : 2);
        ReviewScreenshotStat.Visibility = available
            ? Visibility.Visible : Visibility.Collapsed;
        ReviewWatchStat.Visibility = available
            ? Visibility.Visible : Visibility.Collapsed;
        var statWidth = available
            ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        ReviewStatsGrid.ColumnDefinitions[2].Width = statWidth;
        ReviewStatsGrid.ColumnDefinitions[3].Width = statWidth;
        ArchiveTimelineTitle.Text = available
            ? "观看时间线" : "档案时间线";
        AddManualWatchButton.Visibility = available
            ? Visibility.Visible : Visibility.Collapsed;
        WatchHistoryFilterItem.Visibility = available
            ? Visibility.Visible : Visibility.Collapsed;
        if (!available)
        {
            if (ArchiveTimelineFilter.SelectedIndex == 2)
                ArchiveTimelineFilter.SelectedIndex = 0;
            _details.ApplyPlaybackAvailability(false);
        }
    }

    private async void OnExportReviewClick(
        object sender,
        RoutedEventArgs e)
    {
        var year = double.IsNaN(ReviewYear.Value)
            ? DateTime.Now.Year : (int)ReviewYear.Value;
        var playbackAvailable = _playbackLauncher.IsAvailable;
        var navigationVersion = _navigationVersion;
        var picker = new FileSavePicker
        {
            SuggestedFileName = $"AniMeido-{year}-年度回顾",
        };
        picker.FileTypeChoices.Add("HTML", [".html"]);
        if (!InitializePicker(picker))
        {
            return;
        }
        var file = await picker.PickSaveFileAsync();
        if (file is null)
            return;

        var button = sender as Button;
        if (button is not null)
            button.IsEnabled = false;
        try
        {
            await _reviewExporter.ExportAsync(file.Path, year, playbackAvailable);
            if (navigationVersion == _navigationVersion)
                ShowStatus("年度回顾已导出。", InfoBarSeverity.Success);
        }
        catch (PersonalDataBusyException ex)
        {
            if (navigationVersion == _navigationVersion)
                ShowStatus(ex.Message, InfoBarSeverity.Warning);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            _logger.LogError(ex, "Annual review export failed.");
            if (navigationVersion == _navigationVersion)
                ShowStatus("年度回顾未能导出，请重试。", InfoBarSeverity.Error);
        }
        finally
        {
            if (button is not null)
                button.IsEnabled = true;
        }
    }

    private async void OnOpenScreenshotClick(
        object sender,
        RoutedEventArgs e)
    {
        if (GetDetailScreenshot() is { FileExists: true } item)
        {
            try
            {
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(
                    item.FilePath);
                await Windows.System.Launcher.LaunchFileAsync(file);
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException
                or System.Runtime.InteropServices.COMException)
            {
                ShowStatus("原图当前无法打开。", InfoBarSeverity.Warning);
            }
        }
    }

    private async void OnEditScreenshotClick(
        object sender,
        RoutedEventArgs e)
    {
        if (GetDetailScreenshot() is not { } item)
        {
            return;
        }

        var animeId = new NumberBox
        {
            Header = "Bangumi ID（留空表示未关联）",
            Minimum = 1,
            Value = item.AnimeId ?? double.NaN,
        };
        var title = new TextBox
        {
            Header = "番剧标题",
            Text = item.AnimeTitle ?? string.Empty,
        };
        NumberBox? episode = null;
        if (_playbackLauncher.IsAvailable)
        {
            episode = new NumberBox
            {
                Header = "集数",
                Minimum = 1,
                Value = item.EpisodeNumber ?? double.NaN,
            };
        }
        var context = new TextBox
        {
            Header = "截图备注（可选）",
            PlaceholderText = "比如当时的想法",
            Text = GetScreenshotNote(item),
        };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(animeId);
        panel.Children.Add(title);
        if (episode is not null)
            panel.Children.Add(episode);
        panel.Children.Add(context);
        var dialog = CreateDialog("编辑截图", panel, "保存");
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await _archive.UpdateScreenshotMetadataAsync(
            item.ScreenshotId,
            double.IsNaN(animeId.Value) ? null : (int)animeId.Value,
            title.Text,
            episode is null ? item.EpisodeNumber
                : double.IsNaN(episode.Value) ? null : (int)episode.Value,
            context.Text);
        await RefreshPanelsAsync(
            ArchivePanelKind.Screenshots,
            ArchivePanelKind.Archives,
            ArchivePanelKind.Statistics,
            ArchivePanelKind.Review);
    }

    private async void OnAddSelectedScreenshotTagClick(
        object sender,
        RoutedEventArgs e)
    {
        if (GetDetailScreenshot() is not { } item)
            return;

        var input = new TextBox { Header = "个人标签（逗号分隔）" };
        var dialog = CreateDialog("添加截图标签", input, "添加");
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        var lifetime = _pageLifetime;
        var saved = false;
        try
        {
            await _archive.AddScreenshotTagsAsync(
                [item.ScreenshotId], SplitTags(input.Text));
            saved = true;
            var tags = await _archive.GetScreenshotTagsAsync(item.ScreenshotId);
            if (lifetime is null || !ReferenceEquals(_pageLifetime, lifetime)
                || lifetime.IsCancellationRequested)
            {
                Invalidate(ArchivePanelKind.Screenshots);
                return;
            }
            _panels.ReplaceScreenshotTags(item.ScreenshotId, tags);
            ApplyScreenshotFilter();
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            if (saved)
                Invalidate(ArchivePanelKind.Screenshots);
            if (ReferenceEquals(_pageLifetime, lifetime))
                ShowStatus(saved
                    ? "标签已保存，但列表未能刷新。"
                    : ex.Message,
                    saved ? InfoBarSeverity.Warning : InfoBarSeverity.Error);
        }
    }

    private async void OnRemoveScreenshotTagClick(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: string tag } button
            || GetDetailScreenshot() is not { } item)
            return;

        var lifetime = _pageLifetime;
        button.IsEnabled = false;
        var saved = false;
        try
        {
            await _archive.RemoveScreenshotTagAsync(item.ScreenshotId, tag);
            saved = true;
            var tags = await _archive.GetScreenshotTagsAsync(item.ScreenshotId);
            if (lifetime is null || !ReferenceEquals(_pageLifetime, lifetime)
                || lifetime.IsCancellationRequested)
            {
                Invalidate(ArchivePanelKind.Screenshots);
                return;
            }
            _panels.ReplaceScreenshotTags(item.ScreenshotId, tags);
            ApplyScreenshotFilter();
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            if (saved)
                Invalidate(ArchivePanelKind.Screenshots);
            if (ReferenceEquals(_pageLifetime, lifetime))
                ShowStatus(saved
                    ? "标签已移除，但列表未能刷新。"
                    : ex.Message,
                    saved ? InfoBarSeverity.Warning : InfoBarSeverity.Error);
            button.IsEnabled = true;
        }
    }

    private async void OnTagScreenshotsClick(
        object sender,
        RoutedEventArgs e)
    {
        var selected = ScreenshotList.SelectedItems
            .OfType<AnimeScreenshot>()
            .ToArray();
        if (selected.Length == 0)
        {
            return;
        }

        var input = new TextBox
        {
            Header = "个人标签（逗号分隔）",
        };
        var dialog = CreateDialog("批量添加标签", input, "添加");
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await _archive.AddScreenshotTagsAsync(
                selected.Select(item => item.ScreenshotId).ToArray(),
                SplitTags(input.Text));
            await RefreshPanelsAsync(
                ArchivePanelKind.Screenshots,
                ArchivePanelKind.Archives,
                ArchivePanelKind.Statistics,
                ArchivePanelKind.Review);
            ShowStatus("截图标签已更新。", InfoBarSeverity.Success);
        }
    }

    private async void OnExportScreenshotsClick(
        object sender,
        RoutedEventArgs e)
    {
        var selected = ScreenshotList.SelectedItems
            .OfType<AnimeScreenshot>()
            .Where(item => item.FileExists)
            .ToArray();
        await ExportScreenshotItemsAsync(selected);
    }

    private async void OnExportCurrentScreenshotClick(
        object sender,
        RoutedEventArgs e)
    {
        if (GetDetailScreenshot() is { FileExists: true } item)
            await ExportScreenshotItemsAsync([item]);
    }

    private async Task ExportScreenshotItemsAsync(
        IReadOnlyList<AnimeScreenshot> selected)
    {
        if (selected.Count == 0)
            return;

        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        if (!InitializePicker(picker))
        {
            return;
        }
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null)
        {
            return;
        }

        var exported = 0;
        foreach (var item in selected)
        {
            try
            {
                var file = await Windows.Storage.StorageFile
                    .GetFileFromPathAsync(item.FilePath);
                await file.CopyAsync(
                    folder,
                    file.Name,
                    Windows.Storage.NameCollisionOption.GenerateUniqueName);
                exported++;
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException
                or System.Runtime.InteropServices.COMException)
            {
                // Continue exporting other selected screenshots.
            }
        }

        ShowStatus(
            exported == selected.Count
                ? $"已复制 {exported} 张原图。"
                : $"已复制 {exported} 张，{selected.Count - exported} 张未成功。",
            exported == selected.Count
                ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
    }

    private async void OnDeleteScreenshotClick(
        object sender,
        RoutedEventArgs e)
    {
        var selected = ScreenshotList.SelectedItems
            .OfType<AnimeScreenshot>()
            .ToList();
        if (selected.Count == 0)
        {
            return;
        }

        var (deleted, failed) = await _screenshots.DeleteManyAsync(selected);

        await RefreshPanelsAsync(
            ArchivePanelKind.Screenshots,
            ArchivePanelKind.Archives,
            ArchivePanelKind.Statistics,
            ArchivePanelKind.Review);
        ShowStatus(
            failed == 0
                ? $"{deleted} 张截图已移入回收站。"
                : $"已删除 {deleted} 张，{failed} 张失败且保留记录。",
            failed == 0
                ? InfoBarSeverity.Success
                : InfoBarSeverity.Warning);
    }

    private async void OnCleanupScreenshotsClick(
        object sender,
        RoutedEventArgs e)
    {
        var count = await _archive.RemoveMissingScreenshotRecordsAsync();
        await RefreshPanelsAsync(
            ArchivePanelKind.Screenshots,
            ArchivePanelKind.Archives,
            ArchivePanelKind.Statistics,
            ArchivePanelKind.Review);
        ShowStatus($"已清理 {count} 条缺失文件记录。",
            InfoBarSeverity.Success);
    }

    private async void OnScreenshotSettingsClick(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not Control button || _screenshotSettings.IsEditing
            || _pageLifetime is not { } lifetime)
            return;

        var navigationVersion = _navigationVersion;
        var cancellationToken = lifetime.Token;
        button.IsEnabled = false;
        try
        {
            var saved = await _screenshotSettings.EditAsync(
                settings => ShowScreenshotSettingsAsync(settings, navigationVersion, lifetime),
                cancellationToken);
            if (saved is not null)
                _shortcut.SetEnabled(saved.Enabled);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ArgumentException)
        {
            if (IsNavigationCurrent(navigationVersion, lifetime))
                ShowStatus("截图保存目录无效，请检查后重试。", InfoBarSeverity.Warning);
        }
#pragma warning disable CA1031 // 此事件最终报告设置读取或保存失败，并恢复编辑入口。
        catch (Exception error)
        {
            _logger.LogError(error, "Screenshot settings edit failed");
            if (IsNavigationCurrent(navigationVersion, lifetime))
                ShowStatus("截图设置未能完成，请重试。", InfoBarSeverity.Error);
        }
#pragma warning restore CA1031
        finally
        {
            button.IsEnabled = true;
        }
    }

    private async Task<ScreenshotSettings?> ShowScreenshotSettingsAsync(
        ScreenshotSettings settings,
        int navigationVersion,
        CancellationTokenSource lifetime)
    {
        if (!IsNavigationCurrent(navigationVersion, lifetime))
            return null;

        var enabled = new CheckBox
        {
            Content = "启用 F12 全局截图（拦截 F12）",
            IsChecked = settings.Enabled,
        };
        var sound = new CheckBox
        {
            Content = "播放截图音效",
            IsChecked = settings.SoundEnabled,
        };
        var popup = new CheckBox
        {
            Content = "显示截图缩略图弹窗",
            IsChecked = settings.PopupEnabled,
        };
        var root = new TextBox
        {
            Header = "新截图保存目录",
            Text = settings.RootDirectory,
        };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(enabled);
        panel.Children.Add(sound);
        panel.Children.Add(popup);
        panel.Children.Add(root);
        var dialog = CreateDialog("截图设置", panel, "保存");
        _screenshotSettingsDialog = dialog;
        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary
                || string.IsNullOrWhiteSpace(root.Text))
                return null;

            return new ScreenshotSettings(
                enabled.IsChecked == true,
                Path.GetFullPath(
                    Environment.ExpandEnvironmentVariables(root.Text.Trim())),
                sound.IsChecked == true,
                popup.IsChecked == true);
        }
        finally
        {
            _screenshotSettingsDialog = null;
        }
    }

    private void HideScreenshotSettingsDialog()
    {
        try
        {
            _screenshotSettingsDialog?.Hide();
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException
            or InvalidOperationException)
        {
            _logger.LogWarning(error, "Screenshot settings dialog could not be hidden during navigation");
        }
    }

    private ContentDialog CreateDialog(
        string title,
        object content,
        string primaryText)
        => new()
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = content,
            PrimaryButtonText = primaryText,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        StatusInfoBar.Message = message;
        StatusInfoBar.Severity = severity;
        StatusInfoBar.IsOpen = true;
    }

    private static IEnumerable<string> SplitTags(string value)
        => value.Split(
            [',', '，'],
            StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries);

    private bool InitializePicker(object picker)
    {
        if (!_windowHandleProvider.TryGetWindowHandle(out var handle))
        {
            ShowStatus(
                "主窗口句柄当前不可用，无法打开选择器。",
                InfoBarSeverity.Warning);
            return false;
        }

        InitializeWithWindow.Initialize(picker, handle);
        return true;
    }
}

public sealed record ArchiveTagBarData(string Name, int Count, double Percent);

public sealed class ScreenshotDayGroup : List<AnimeScreenshot>
{
    public ScreenshotDayGroup(
        DateTime date,
        IEnumerable<AnimeScreenshot> screenshots)
        : base(screenshots)
    {
        Header = $"{date:yyyy年M月d日} · {Count} 张";
    }

    public string Header { get; }
}
