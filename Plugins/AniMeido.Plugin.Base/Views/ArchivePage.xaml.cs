using AniMeido.Contracts;
using AniMeido.Contracts.Desktop;
using AniMeido.Contracts.Playback;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using Microsoft.Data.Sqlite;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace AniMeido.Plugin.Base.Views;

public sealed partial class ArchivePage : Page, INavigationAware
{
    private readonly ArchiveService _archive;
    private readonly IAnimePlaybackLauncher _playbackLauncher;
    private readonly IAnimeDataSource _animeDataSource;
    private readonly ScreenshotArchiveService _screenshots;
    private readonly ScreenshotShortcutAction _shortcut;
    private readonly IWindowHandleProvider _windowHandleProvider;
    private IReadOnlyList<ArchiveListItem> _allArchives = [];
    private IReadOnlyList<AnimeScreenshot> _allScreenshots = [];
    private readonly Dictionary<string, IReadOnlyList<string>>
        _screenshotTags = new(StringComparer.Ordinal);
    private readonly Dictionary<ArchivePanelKind, PanelLoadState>
        _panelStates = Enum.GetValues<ArchivePanelKind>()
            .ToDictionary(kind => kind, _ => new PanelLoadState());
    private ArchiveListItem? _selectedArchive;
    private IReadOnlyList<ArchiveTimelineItem> _timelineItems = [];
    private readonly Dictionary<int, Task<AniMeido.Contracts.Models.Anime?>>
        _coverRequests = [];
    private readonly SemaphoreSlim _coverFetchLimit = new(4);
    private string? _requestedScreenshotId;
    private int? _requestedAnimeId;
    private CancellationTokenSource? _pageLifetime;
    private CancellationTokenSource? _selectionCancellation;
    private int _selectionVersion;
    private ArchivePanelKind _activePanel = ArchivePanelKind.Archives;
    private bool _suppressReviewYearChanged = true;
    private bool _navigationInitialized;
    private int _navigationVersion;
    private bool _isPlaybackAvailabilitySubscribed;

    public ArchivePage(
        ArchiveService archive,
        IAnimeDataSource animeDataSource,
        IAnimePlaybackLauncher playbackLauncher,
        ScreenshotArchiveService screenshots,
        ScreenshotShortcutAction shortcut,
        IWindowHandleProvider windowHandleProvider)
    {
        _archive = archive;
        _animeDataSource = animeDataSource;
        _playbackLauncher = playbackLauncher;
        _screenshots = screenshots;
        _shortcut = shortcut;
        _windowHandleProvider = windowHandleProvider;
        InitializeComponent();
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

    private async Task ConfigureArchiveCoverAsync(
        Image? image,
        int decodeWidth = 56)
    {
        var animeId = image?.DataContext switch
        {
            ArchiveListItem item => item.Archive.AnimeId,
            AnnualReviewMoment moment => moment.AnimeId,
            _ => (int?)null,
        };
        if (image is null || animeId is null || _pageLifetime is null)
        {
            if (image is not null)
                ManagedImageLoader.Cancel(image);
            return;
        }

        var cancellationToken = _pageLifetime.Token;
        ManagedImageLoader.Cancel(image);
        try
        {
            if (!_coverRequests.TryGetValue(animeId.Value, out var request))
            {
                request = FetchArchiveCoverAsync(animeId.Value, cancellationToken);
                _coverRequests.Add(animeId.Value, request);
            }

            var anime = await request;
            if (!cancellationToken.IsCancellationRequested
                && (image.DataContext switch
                {
                    ArchiveListItem current => current.Archive.AnimeId == animeId,
                    AnnualReviewMoment current => current.AnimeId == animeId,
                    _ => false,
                }))
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
        catch (Exception ex) when (ex is HttpRequestException
            or IOException or InvalidOperationException
            or TaskCanceledException or JsonException)
        {
            _coverRequests.Remove(animeId.Value);
            ManagedImageLoader.Cancel(image);
        }
    }

    private async Task<AniMeido.Contracts.Models.Anime?>
        FetchArchiveCoverAsync(int animeId, CancellationToken cancellationToken)
    {
        await _coverFetchLimit.WaitAsync(cancellationToken);
        try
        {
            return await _animeDataSource.GetAnimeDetailAsync(
                animeId,
                cancellationToken);
        }
        finally
        {
            _coverFetchLimit.Release();
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

    private static void ConfigureScreenshotImage(Image? image)
    {
        if (image?.DataContext is AnimeScreenshot screenshot)
            ManagedImageLoader.ConfigureLocal(image, screenshot.FilePath, 260);
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
        ShowPanel(requestedScreenshotId is null ? "archives" : "screenshots");
        var lifetime = _pageLifetime;
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
    }

    private async Task EnsureActivePanelAsync()
    {
        if (!_navigationInitialized || _pageLifetime is null)
        {
            return;
        }

        var panel = _activePanel;
        var lifetime = _pageLifetime;
        var state = _panelStates[panel];
        var generation = state.Generation;
        try
        {
            var load = state.EnsureAsync(
                (generation, cancellationToken) => LoadPanelAsync(
                    panel,
                    generation,
                    cancellationToken),
                lifetime.Token);
            generation = state.Generation;
            await load;
            if (IsPanelCurrent(panel, generation, lifetime))
            {
                StatusInfoBar.IsOpen = false;
            }
        }
        catch (OperationCanceledException)
            when (lifetime.IsCancellationRequested
                || generation != state.Generation
                || !ReferenceEquals(_pageLifetime, lifetime)
                || !state.IsCurrent(generation, lifetime.Token))
        {
        }
        catch (Exception ex) when (
            ex is InvalidOperationException
                or IOException
                or Microsoft.Data.Sqlite.SqliteException)
        {
            if (IsPanelCurrent(panel, generation, lifetime))
            {
                ShowStatus(ex.Message, InfoBarSeverity.Error);
            }
        }
    }

    private bool IsPanelCurrent(
        ArchivePanelKind panel,
        int generation,
        CancellationTokenSource lifetime)
        => _navigationInitialized
            && ReferenceEquals(_pageLifetime, lifetime)
            && _activePanel == panel
            && _panelStates[panel].IsCurrent(generation, lifetime.Token);

    private bool IsPanelResultCurrent(
        ArchivePanelKind panel,
        int generation,
        CancellationToken cancellationToken)
        => _navigationInitialized
            && _activePanel == panel
            && _pageLifetime is not null
            && _panelStates[panel].IsCurrent(generation, cancellationToken);

    private bool IsNavigationCurrent(
        int navigationVersion,
        CancellationTokenSource? lifetime)
        => navigationVersion == _navigationVersion
            && lifetime is not null
            && ReferenceEquals(_pageLifetime, lifetime)
            && !lifetime.IsCancellationRequested;

    private async Task LoadPanelAsync(
        ArchivePanelKind panel,
        int generation,
        CancellationToken cancellationToken)
    {
        switch (panel)
        {
            case ArchivePanelKind.Archives:
                var archives = await _archive.GetArchiveListAsync(
                    cancellationToken);
                if (IsPanelResultCurrent(
                        panel,
                        generation,
                        cancellationToken))
                {
                    _allArchives = archives;
                    ApplyArchiveFilter();
                }
                break;
            case ArchivePanelKind.Statistics:
                var statistics = await _archive.GetStatisticsAsync(
                    cancellationToken: cancellationToken);
                if (IsPanelResultCurrent(
                        panel,
                        generation,
                        cancellationToken))
                {
                    RenderStatistics(statistics);
                }
                break;
            case ArchivePanelKind.Review:
                var year = double.IsNaN(ReviewYear.Value)
                    ? DateTime.Now.Year
                    : (int)ReviewYear.Value;
                var reviewTask = _archive.GetStatisticsAsync(
                    year, cancellationToken);
                var reviewArchivesTask = _archive.GetArchiveListAsync(
                    cancellationToken);
                var momentsTask = _archive.GetReviewMomentsAsync(
                    year, cancellationToken: cancellationToken);
                var reviewScreenshotsTask = _archive.GetScreenshotsAsync(
                    cancellationToken: cancellationToken,
                    year: year);
                await Task.WhenAll(
                    reviewTask, reviewArchivesTask, momentsTask,
                    reviewScreenshotsTask);
                if (IsPanelResultCurrent(
                        panel,
                        generation,
                        cancellationToken))
                {
                    RenderReview(
                        year,
                        await reviewTask,
                        await reviewArchivesTask,
                        await momentsTask,
                        await reviewScreenshotsTask);
                }
                break;
            case ArchivePanelKind.Screenshots:
                var screenshotsTask = _archive.GetScreenshotsAsync(
                    cancellationToken: cancellationToken);
                var tagsTask = _archive.GetAllScreenshotTagsAsync(
                    cancellationToken);
                await Task.WhenAll(screenshotsTask, tagsTask);
                if (IsPanelResultCurrent(
                        panel,
                        generation,
                        cancellationToken))
                {
                    _allScreenshots = await screenshotsTask;
                    var tagsByScreenshot = await tagsTask;
                    _screenshotTags.Clear();
                    foreach (var screenshot in _allScreenshots)
                    {
                        _screenshotTags[screenshot.ScreenshotId] =
                            tagsByScreenshot.GetValueOrDefault(
                                screenshot.ScreenshotId) ?? [];
                    }

                    ApplyScreenshotFilter();
                }
                break;
        }
    }

    private void Invalidate(params ArchivePanelKind[] panels)
    {
        foreach (var panel in panels)
        {
            _panelStates[panel].Invalidate();
        }
    }

    private async Task RefreshPanelsAsync(
        params ArchivePanelKind[] panels)
    {
        Invalidate(panels);
        if (panels.Contains(_activePanel))
        {
            await EnsureActivePanelAsync();
        }
    }

    private async Task LoadAsync()
    {
        Invalidate(_panelStates.Keys.ToArray());
        await EnsureActivePanelAsync();
    }

    private void ApplyScreenshotFilter()
    {
        var selectedIds = ScreenshotList.SelectedItems
            .OfType<AnimeScreenshot>()
            .Select(item => item.ScreenshotId)
            .ToHashSet(StringComparer.Ordinal);
        var filter = ScreenshotFilter.Text.Trim();
        var year = double.IsNaN(ScreenshotYearFilter.Value)
            ? null
            : (int?)ScreenshotYearFilter.Value;
        var filteredScreenshots = _allScreenshots.Where(item =>
            (year is null
                || item.CapturedAt.ToLocalTime().Year == year)
            && (filter.Length == 0
                || (item.AnimeTitle?.Contains(
                    filter,
                    StringComparison.CurrentCultureIgnoreCase) ?? false)
                || item.ContextNote.Contains(
                    filter,
                    StringComparison.CurrentCultureIgnoreCase)
                || item.ProcessName.Contains(
                    filter,
                    StringComparison.CurrentCultureIgnoreCase)
                || (_screenshotTags.TryGetValue(
                        item.ScreenshotId,
                        out var tags)
                    && tags.Any(tag => tag.Contains(
                        filter,
                        StringComparison.CurrentCultureIgnoreCase)))))
            .ToArray();
        ScreenshotList.ItemsSource = filteredScreenshots;
        foreach (var item in filteredScreenshots.Where(item =>
                     selectedIds.Contains(item.ScreenshotId)))
        {
            ScreenshotList.SelectedItems.Add(item);
        }
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
        IReadOnlyList<ArchiveListItem> archives,
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

        var covers = archives
            .Where(item => item.Archive.CreatedAt.ToLocalTime().Year == year)
            .OrderByDescending(item => item.Archive.CreatedAt)
            .Take(3)
            .ToArray();
        ReviewCovers.ItemsSource = covers;
        ReviewCoversEmpty.Visibility = covers.Length == 0
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
        var selectedAnimeId = _selectedArchive?.Archive.AnimeId;
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
        var filteredArchives = _allArchives.Where(item =>
            item.TrackingStatus
                != AniMeido.Contracts.Models.AnimeTrackingStatus.Blocked
            && (string.IsNullOrWhiteSpace(filter)
                || item.Archive.TitleSnapshot.Contains(
                    filter,
                    StringComparison.CurrentCultureIgnoreCase)
                || item.Tags.Any(tag => tag.Contains(
                    filter,
                    StringComparison.CurrentCultureIgnoreCase)))
            && (status is null || item.TrackingStatus == status)
            && (minimumRating is null
                || item.Archive.PersonalRating >= minimumRating)
            && (year is null
                || item.Archive.CreatedAt.ToLocalTime().Year == year))
            .ToList();
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
        else if (_selectedArchive is not null)
        {
            _selectedArchive = null;
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
    {
        _selectionCancellation?.Cancel();
        _selectionCancellation?.Dispose();
        _selectionCancellation = new CancellationTokenSource();
        var cancellationToken = _selectionCancellation.Token;
        var selectionVersion = Interlocked.Increment(ref _selectionVersion);
        var selectedArchive = ArchiveList.SelectedItem as ArchiveListItem;
        _selectedArchive = selectedArchive;
        if (selectedArchive is null)
        {
            ArchiveSelectionEmptyState.Visibility = Visibility.Visible;
            ArchiveDetailPanel.Visibility = Visibility.Collapsed;
            EntryList.ItemsSource = null;
            _timelineItems = [];
            ArchiveScreenshotStrip.ItemsSource = null;
            ArchiveScreenshotSection.Visibility = Visibility.Collapsed;
            ManagedImageLoader.Cancel(ArchiveHeroCover);
            UpdateEntryActions();
            return;
        }

        ArchiveSelectionEmptyState.Visibility = Visibility.Collapsed;
        ArchiveDetailPanel.Visibility = Visibility.Visible;
        ArchiveTitle.Text = selectedArchive.Archive.TitleSnapshot;
        ArchiveAnimeMetadata.Text = $"Bangumi #{selectedArchive.Archive.AnimeId}";
        ArchiveRatingText.Text = selectedArchive.RatingText;
        ArchiveStatusText.Text = selectedArchive.StatusText;
        ArchiveTagsText.Text = selectedArchive.Tags.Count == 0
            ? "尚无个人标签"
            : string.Join(" · ", selectedArchive.Tags);
        ArchiveMarkdownRenderer.Render(
            ArchiveNotePreview,
            selectedArchive.Archive.SummaryNote);
        ArchiveScreenshotStrip.ItemsSource = null;
        ArchiveScreenshotSection.Visibility = Visibility.Collapsed;
        ArchiveScreenshotCount.Text = string.Empty;
        ManagedImageLoader.Cancel(ArchiveHeroCover);
        _ = ConfigureHeroCoverAsync(
            selectedArchive.Archive.AnimeId,
            selectionVersion,
            cancellationToken);
        try
        {
            var animeId = selectedArchive.Archive.AnimeId;
            var entriesTask = _archive.GetEntriesAsync(animeId, cancellationToken);
            var historyTask = _playbackLauncher.IsAvailable
                ? _archive.GetWatchHistoryAsync(animeId, cancellationToken)
                : Task.FromResult<IReadOnlyList<WatchHistoryItem>>([]);
            var changesTask = _archive.GetTrackingChangesAsync(animeId, cancellationToken);
            var screenshotsTask = _archive.GetScreenshotsAsync(animeId, cancellationToken);
            await Task.WhenAll(entriesTask, historyTask, changesTask, screenshotsTask);
            if (selectionVersion != _selectionVersion
                || _selectedArchive?.Archive.AnimeId
                    != animeId)
            {
                return;
            }
            SetTimeline(await entriesTask, await historyTask, await changesTask);
            var screenshots = (await screenshotsTask)
                .Where(item => item.FileExists)
                .ToArray();
            ArchiveScreenshotCount.Text = $"{screenshots.Length} 张";
            ArchiveScreenshotSection.Visibility = screenshots.Length > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            ArchiveScreenshotStrip.ItemsSource = screenshots.Take(2).ToArray();
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is SqliteException
            or IOException or InvalidOperationException)
        {
            if (selectionVersion == _selectionVersion)
                ShowStatus($"档案详情加载失败：{ex.Message}", InfoBarSeverity.Warning);
        }
    }

    private async Task ConfigureHeroCoverAsync(
        int animeId,
        int selectionVersion,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!_coverRequests.TryGetValue(animeId, out var request))
            {
                request = FetchArchiveCoverAsync(
                    animeId,
                    _pageLifetime?.Token ?? cancellationToken);
                _coverRequests.Add(animeId, request);
            }
            var anime = await request;
            if (selectionVersion == _selectionVersion
                && !cancellationToken.IsCancellationRequested)
            {
                ArchiveAnimeMetadata.Text = anime is null
                    ? $"Bangumi #{animeId}"
                    : string.Join(" · ", new[]
                    {
                        anime.AirDate?.Year.ToString(),
                        anime.Studio,
                        $"Bangumi #{animeId}",
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
        catch (Exception ex) when (ex is HttpRequestException
            or IOException or InvalidOperationException
            or TaskCanceledException or JsonException)
        {
            _coverRequests.Remove(animeId);
        }
    }

    private void SetTimeline(
        IReadOnlyList<ArchiveEntry> entries,
        IReadOnlyList<WatchHistoryItem> history,
        IReadOnlyList<ArchiveTrackingChange> changes)
    {
        _timelineItems = entries.Select(entry => new ArchiveTimelineItem(
                entry.OccurredAt,
                "感想",
                string.IsNullOrWhiteSpace(entry.EpisodeText)
                    ? "观看感想"
                    : entry.EpisodeText,
                entry.Body,
                entry))
            .Concat((_playbackLauncher.IsAvailable ? history : [])
                .Select(item => new ArchiveTimelineItem(
                item.OccurredAt,
                "观看记录",
                $"{item.EpisodeText} · {item.SourceText}",
                item.Note)))
            .Concat(changes.Select(change => new ArchiveTimelineItem(
                change.ChangedAt,
                "状态变更",
                $"标记为{FormatTrackingStatus(change.NewStatus)}",
                string.Empty)))
            .OrderByDescending(item => item.OccurredAt)
            .ToArray();
        ApplyTimelineFilter();
    }

    private static string FormatTrackingStatus(
        AniMeido.Contracts.Models.AnimeTrackingStatus status)
        => status switch
        {
            AniMeido.Contracts.Models.AnimeTrackingStatus.Watching => "追番中",
            AniMeido.Contracts.Models.AnimeTrackingStatus.PlanToWatch => "补番中",
            AniMeido.Contracts.Models.AnimeTrackingStatus.Following => "关注",
            AniMeido.Contracts.Models.AnimeTrackingStatus.Completed => "已看完",
            AniMeido.Contracts.Models.AnimeTrackingStatus.Dropped => "弃坑",
            AniMeido.Contracts.Models.AnimeTrackingStatus.NotInterested => "不感兴趣",
            AniMeido.Contracts.Models.AnimeTrackingStatus.Blocked => "屏蔽",
            _ => "无标记",
        };

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
            ? _timelineItems
            : _timelineItems.Where(item => item.Kind == kind).ToArray();
        UpdateEntryActions();
    }

    private async Task RefreshSelectedTimelineAsync(int animeId)
    {
        if (_selectedArchive?.Archive.AnimeId != animeId
            || _selectionCancellation is null)
            return;

        var selectionVersion = _selectionVersion;
        var cancellationToken = _selectionCancellation.Token;
        try
        {
            var entriesTask = _archive.GetEntriesAsync(animeId, cancellationToken);
            var historyTask = _playbackLauncher.IsAvailable
                ? _archive.GetWatchHistoryAsync(animeId, cancellationToken)
                : Task.FromResult<IReadOnlyList<WatchHistoryItem>>([]);
            var changesTask = _archive.GetTrackingChangesAsync(animeId, cancellationToken);
            await Task.WhenAll(entriesTask, historyTask, changesTask);
            if (selectionVersion == _selectionVersion
                && _selectedArchive?.Archive.AnimeId == animeId)
            {
                SetTimeline(await entriesTask, await historyTask, await changesTask);
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async void OnEditArchiveClick(
        object sender,
        RoutedEventArgs e)
    {
        if (_selectedArchive is null)
        {
            ShowStatus("请先选择一部番剧。", InfoBarSeverity.Warning);
            return;
        }

        var archive = _selectedArchive;
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
            try
            {
                await _archive.UpsertArchiveAsync(
                    archive.Archive.AnimeId,
                    archive.Archive.TitleSnapshot,
                    double.IsNaN(rating.Value) ? null : rating.Value,
                    archive.Archive.SummaryNote);
                await _archive.SetAnimeTagsAsync(
                    archive.Archive.AnimeId,
                    SplitTags(tags.Text));
                await RefreshPanelsAsync(
                    ArchivePanelKind.Archives,
                    ArchivePanelKind.Statistics,
                    ArchivePanelKind.Review);
                ShowStatus("档案已保存。", InfoBarSeverity.Success);
            }
            catch (Exception ex) when (ex is SqliteException
                or IOException or InvalidOperationException
                or ArgumentException)
            {
                args.Cancel = true;
                ShowStatus(
                    $"保存未全部完成，请刷新确认：{ex.Message}",
                    InfoBarSeverity.Warning);
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
        if (_selectedArchive is not { } selected)
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
        source.TextChanged += (_, _) =>
            ArchiveMarkdownRenderer.Render(preview, source.Text);
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
                if (_selectedArchive?.Archive.AnimeId == selected.Archive.AnimeId)
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
                ShowStatus($"笔记保存失败：{ex.Message}", InfoBarSeverity.Warning);
            }
            finally
            {
                deferral.Complete();
            }
        };
        await dialog.ShowAsync();
    }

    private async void OnAddEntryClick(object sender, RoutedEventArgs e)
    {
        if (_selectedArchive is not { } selected)
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

        if (_selectedArchive is not { } selected)
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
        if (_activePanel != nextPanel
            && _panelStates[_activePanel].IsLoading)
        {
            _panelStates[_activePanel].CancelInflight();
        }
        _activePanel = nextPanel;
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
        if (!_isPlaybackAvailabilitySubscribed)
        {
            _playbackLauncher.AvailabilityChanged += OnPlaybackAvailabilityChanged;
            _isPlaybackAvailabilitySubscribed = true;
        }
        ApplyPlaybackAvailability();
        if (_selectedArchive is { } selected && _pageLifetime is { } lifetime)
        {
            _ = ConfigureHeroCoverAsync(
                selected.Archive.AnimeId,
                _selectionVersion,
                lifetime.Token);
        }
        _ = EnsureActivePanelAsync();
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        // Once initialized, this remains true because GoBack can restore the
        // page instance without calling OnNavigatedToAsync again.
        Interlocked.Increment(ref _navigationVersion);
        Interlocked.Increment(ref _selectionVersion);
        _pageLifetime?.Cancel();
        _pageLifetime?.Dispose();
        _pageLifetime = null;
        foreach (var state in _panelStates.Values)
        {
            state.CancelInflight();
        }
        _selectionCancellation?.Cancel();
        _selectionCancellation?.Dispose();
        _selectionCancellation = null;
        if (_isPlaybackAvailabilitySubscribed)
        {
            _playbackLauncher.AvailabilityChanged -= OnPlaybackAvailabilityChanged;
            _isPlaybackAvailabilitySubscribed = false;
        }
        _coverRequests.Clear();
        ManagedImageLoader.Cancel(ArchiveHeroCover);
    }

    private void OnPlaybackAvailabilityChanged(object? sender, EventArgs e)
        => DispatcherQueue.TryEnqueue(() =>
        {
            ApplyPlaybackAvailability();
            if (_selectedArchive is { } selected)
                _ = RefreshSelectedTimelineAsync(selected.Archive.AnimeId);
        });

    private void ApplyPlaybackAvailability()
    {
        var available = _playbackLauncher.IsAvailable;
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
            _timelineItems = _timelineItems
                .Where(item => item.Kind != "观看记录")
                .ToArray();
            ApplyTimelineFilter();
        }
    }

    private async void OnExportReviewClick(
        object sender,
        RoutedEventArgs e)
    {
        var year = double.IsNaN(ReviewYear.Value)
            ? DateTime.Now.Year : (int)ReviewYear.Value;
        var statistics = await _archive.GetStatisticsAsync(year);
        var moments = await _archive.GetReviewMomentsAsync(year, 8);
        var playbackAvailable = _playbackLauncher.IsAvailable;
        var screenshots = playbackAvailable
            ? (await _archive.GetScreenshotsAsync(year: year))
                .Where(item => item.FileExists)
                .Take(12)
                .ToArray()
            : [];
        var html = await BuildReviewHtmlAsync(
            year,
            statistics,
            moments,
            screenshots,
            playbackAvailable);
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
        if (file is not null)
        {
            await File.WriteAllTextAsync(file.Path, html);
            ShowStatus("年度回顾已导出。", InfoBarSeverity.Success);
        }
    }

    private async void OnOpenScreenshotClick(
        object sender,
        RoutedEventArgs e)
    {
        if (ScreenshotList.SelectedItem is AnimeScreenshot item
            && item.FileExists)
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(
                item.FilePath);
            await Windows.System.Launcher.LaunchFileAsync(file);
        }
    }

    private async void OnEditScreenshotClick(
        object sender,
        RoutedEventArgs e)
    {
        if (ScreenshotList.SelectedItem is not AnimeScreenshot item)
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
        var episode = new NumberBox
        {
            Header = "集数",
            Minimum = 1,
            Value = item.EpisodeNumber ?? double.NaN,
        };
        var context = new TextBox
        {
            Header = "场合备注",
            Text = item.ContextNote,
        };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(animeId);
        panel.Children.Add(title);
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
            double.IsNaN(episode.Value) ? null : (int)episode.Value,
            context.Text);
        await RefreshPanelsAsync(
            ArchivePanelKind.Screenshots,
            ArchivePanelKind.Archives,
            ArchivePanelKind.Statistics,
            ArchivePanelKind.Review);
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
        if (selected.Length == 0)
        {
            return;
        }

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

        foreach (var item in selected)
        {
            var file = await Windows.Storage.StorageFile
                .GetFileFromPathAsync(item.FilePath);
            await file.CopyAsync(
                folder,
                file.Name,
                Windows.Storage.NameCollisionOption.GenerateUniqueName);
        }

        ShowStatus($"已导出 {selected.Length} 张原图。",
            InfoBarSeverity.Success);
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

        var deleted = 0;
        var failed = 0;
        foreach (var item in selected)
        {
            try
            {
                await _screenshots.DeleteAsync(item);
                deleted++;
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException)
            {
                failed++;
            }
        }

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
        var settings = await _archive.GetScreenshotSettingsAsync();
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
        if (await dialog.ShowAsync() == ContentDialogResult.Primary
            && !string.IsNullOrWhiteSpace(root.Text))
        {
            var updated = new ScreenshotSettings(
                enabled.IsChecked == true,
                Path.GetFullPath(
                    Environment.ExpandEnvironmentVariables(root.Text.Trim())),
                sound.IsChecked == true,
                popup.IsChecked == true);
            await _archive.SaveScreenshotSettingsAsync(updated);
            _shortcut.SetEnabled(updated.Enabled);
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

    private static async Task<string> BuildReviewHtmlAsync(
        int year,
        ArchiveStatistics statistics,
        IReadOnlyList<AnnualReviewMoment> moments,
        IReadOnlyList<AnimeScreenshot> screenshots,
        bool playbackAvailable)
    {
        var title = WebUtility.HtmlEncode($"{year} 年 AniMeido 年度回顾");
        var tags = WebUtility.HtmlEncode(string.Join(
            "、",
            statistics.TagCounts.Take(8).Select(item => item.Key)));
        var momentCards = new StringBuilder();
        foreach (var moment in moments)
        {
            var date = WebUtility.HtmlEncode(moment.DateText);
            var anime = WebUtility.HtmlEncode(moment.AnimeTitle);
            var body = WebUtility.HtmlEncode(moment.Entry.Body);
            momentCards.Append(
                $"<article class=\"moment\"><small>{date} · {anime}</small><p>{body}</p></article>");
        }
        var images = new StringBuilder();
        foreach (var screenshot in screenshots)
        {
            try
            {
                var bytes = await File.ReadAllBytesAsync(screenshot.FilePath);
                var caption = WebUtility.HtmlEncode(
                    screenshot.ContextNote.Length > 0
                        ? screenshot.ContextNote
                        : screenshot.AnimeTitle ?? "截图");
                images.Append(
                    $"<figure><img src=\"data:image/png;base64,{Convert.ToBase64String(bytes)}\" alt=\"截图\"><figcaption>{caption}</figcaption></figure>");
            }
            catch (IOException)
            {
                // A screenshot can disappear after it was listed.
            }
        }
        var duration = $"{statistics.EstimatedWatchMinutes / 60} 小时 {statistics.EstimatedWatchMinutes % 60} 分钟";
        var playbackCards = playbackAvailable
            ? $"<div class=\"card\"><strong>{statistics.ScreenshotCount}</strong><small>截图记录</small></div>"
                + $"<div class=\"card\"><strong>{duration}</strong><small>估算观看时长</small></div>"
            : string.Empty;
        var gallery = playbackAvailable
            ? $"<h2>镜头里的回忆</h2><div class=\"gallery\">{images}</div>"
            : string.Empty;
        return $$"""
            <!doctype html><html lang="zh-CN"><head><meta charset="utf-8">
            <title>{{title}}</title><style>
            body{font-family:"Segoe UI","Microsoft YaHei",sans-serif;
            max-width:960px;margin:48px auto;padding:0 24px;
            color:#f2f5f9;background:#202225;line-height:1.6}
            h1{font-size:2.5rem;color:#91ccf5}h2{margin-top:36px}
            .cards{display:grid;grid-template-columns:repeat(2,1fr);gap:12px}
            .card,.moment,figure{background:#303338;border-radius:12px;padding:20px}
            .gallery{display:grid;grid-template-columns:repeat(2,1fr);gap:12px}
            figure{margin:0}img{width:100%;aspect-ratio:16/9;object-fit:cover;
            border-radius:8px}figcaption,small{color:#c8d3dc}
            strong{font-size:2rem;color:#91ccf5;display:block}
            .moment{margin-bottom:10px}.moment p{white-space:pre-wrap;margin-bottom:0}
            </style></head><body><h1>{{title}}</h1>
            <div class="cards">
            <div class="card"><strong>{{statistics.ArchiveCount}}</strong><small>新增档案</small></div>
            <div class="card"><strong>{{statistics.EntryCount}}</strong><small>写下感想</small></div>
            {{playbackCards}}
            </div><h2>这一年留下的片段</h2>{{momentCards}}
            {{gallery}}
            <h2>年度新增档案的个人标签</h2><p>{{tags}}</p></body></html>
            """;
    }

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
