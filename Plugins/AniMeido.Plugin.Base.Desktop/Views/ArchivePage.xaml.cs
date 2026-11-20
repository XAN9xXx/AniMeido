using AniMeido.Contracts;
using AniMeido.Contracts.Desktop;
using AniMeido.Plugin.Base.Exceptions;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;

namespace AniMeido.Plugin.Base.Views;

public sealed partial class ArchivePage : Page, INavigationAware, IDesktopManualRefresh
{
    private readonly ArchiveService _archive;
    private readonly ArchiveReviewExporter _reviewExporter;
    private readonly IAnimeDataSource _animeDataSource;
    private readonly ScreenshotArchiveService _screenshots;
    private readonly ScreenshotShortcutAction _shortcut;
    private readonly IWindowHandleProvider _windowHandleProvider;
    private readonly ILogger<ArchivePage> _logger;
    private readonly PendingReviewViewModel _pendingReview;
    private readonly PersonalIdRemapService _idRemap;
    private readonly CollectionViewSource _screenshotGroups;
    private readonly ArchiveGalleryViewModel _gallery = new();
    private readonly ArchivePanelsViewModel _panels;
    private readonly ArchiveDetailsViewModel _details;
    private readonly ArchiveCoverRequests _coverRequests;
    private readonly DesktopPageOperations _operations = new();
    private readonly DesktopPageOperations _reads = new();
    private CancellationTokenSource? _pageLifetime;
    private ContentDialog? _dialog;
    private int _navigationVersion;
    private int? _requestedAnimeId;
    private string? _requestedScreenshotId;
    private bool _suppressReviewYearChanged = true;
    private bool _suppressScreenshotSelectionChanged;
    private bool _suppressArchiveSelection;
    private Task _manualRefresh = Task.CompletedTask;
    private Task _archiveSelectionRead = Task.CompletedTask;
    private int _readFailures;
    private int _desktopScreenshotPage;
    private int _desktopScreenshotPageCount = 1;
    internal Task PendingActivation { get; private set; } = Task.CompletedTask;
    internal Task PendingOperation => _operations.Pending;
    internal Func<CancellationToken, Task<ScreenshotSettings>> ReadScreenshotSettingsAsync { get; set; }
    internal Func<CancellationToken, Task<IReadOnlyList<LocalAnimeTarget>>> ReadScreenshotTargetsAsync { get; set; }
    internal Task PendingRead => _reads.DrainAsync();

    public ArchivePage(ArchiveService archive, ArchiveQueryService queries, ArchiveReviewExporter reviewExporter,
        IAnimeDataSource animeDataSource, ScreenshotArchiveService screenshots,
        ScreenshotShortcutAction shortcut, IWindowHandleProvider windowHandleProvider, ILogger<ArchivePage> logger,
        PendingReviewService pendingReview, PersonalIdRemapService idRemap)
    {
        _archive = archive;
        ReadScreenshotSettingsAsync = archive.GetScreenshotSettingsAsync;
        _reviewExporter = reviewExporter;
        _animeDataSource = animeDataSource;
        _screenshots = screenshots;
        _shortcut = shortcut;
        _windowHandleProvider = windowHandleProvider;
        _logger = logger;
        _idRemap = idRemap;
        _pendingReview = new(pendingReview, idRemap, animeDataSource);
        ReadScreenshotTargetsAsync = token => _pendingReview.GetTargetsAsync(0, token);
        _panels = new(queries);
        _details = new(queries);
        _coverRequests = new(animeDataSource.GetAnimeDetailAsync, logger);
        _panels.PanelPublished += OnPanelPublished;
        _panels.PanelLoadCompleted += (_, error) => { if (error is not null) ReportReadFailure(error); };
        _details.SelectionChanged += OnArchiveDetailsSelectionChanged;
        _details.TimelinePublished += ApplyTimelineFilter;
        _details.DetailsPublished += OnArchiveDetailsPublished;
        _details.LoadFailed += ReportReadFailure;
        InitializeComponent();
        _screenshotGroups = (CollectionViewSource)Resources["ScreenshotGroupsSource"];
        StatusFilter.SelectedIndex = ArchiveTimelineFilter.SelectedIndex = 0;
        ReviewYear.Value = DateTime.Today.Year;
        _suppressReviewYearChanged = false;
        ShowPanel("archives");
        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
    }

    public Task OnNavigatedToAsync(object? parameter)
    {
        _requestedAnimeId = parameter as int?;
        _requestedScreenshotId = parameter as string;
        if (parameter is not null) ShowPanel(parameter is string ? "screenshots" : "archives");
        if (IsLoaded) PendingActivation = ActivateAsync();
        return PendingActivation;
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e) => PendingActivation = ActivateAsync();

    private async Task ActivateAsync()
    {
        Leave();
        var lifetime = _pageLifetime = new();
        var version = ++_navigationVersion;
        _idRemap.Changed += OnPendingRecordsChanged;
        await _operations.DrainAsync();
        if (!IsNavigationCurrent(version, lifetime)) return;
        _panels.InvalidateAll();
        await ReadAsync(async () =>
        {
            await Task.WhenAll(RefreshPendingAsync(), EnsureActivePanelAsync());
            if (!IsNavigationCurrent(version, lifetime)) return;
            if (_requestedAnimeId is { } id)
            {
                ArchiveList.SelectedItem = ArchiveList.Items.OfType<ArchiveListItem>().FirstOrDefault(a => a.Archive.AnimeId == id);
                _requestedAnimeId = null;
            }
            if (ArchiveList.SelectedItem is ArchiveListItem selected && !_details.HasActiveSelection)
                await _details.SelectAsync(selected);
        });
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e) => Leave();

    private void Leave()
    {
        ++_navigationVersion;
        _idRemap.Changed -= OnPendingRecordsChanged;
        _pageLifetime?.Cancel();
        _pageLifetime?.Dispose();
        _pageLifetime = null;
        _manualRefresh = Task.CompletedTask;
        _dialog?.Hide();
        _panels.CancelInflight();
        _reads.Track(ObserveStopAsync(_details.CancelInflightAsync()));
        _coverRequests.DiscardIncomplete();
        ManagedImageLoader.Cancel(ArchiveHeroCover);
    }

    private async Task ObserveStopAsync(Task stop)
    {
        try { await stop; }
#pragma warning disable CA1031 // 结束旧选择的最终处理边界。
        catch (Exception error) { _logger.LogWarning(error, "Archive selection shutdown failed"); }
#pragma warning restore CA1031
    }

    private bool IsNavigationCurrent(int version, CancellationTokenSource? lifetime)
        => version == _navigationVersion && lifetime is not null && ReferenceEquals(lifetime, _pageLifetime)
            && !lifetime.IsCancellationRequested;

    private Task ReadAsync(Func<Task> read)
    {
        if (_pageLifetime is not { } lifetime) return Task.CompletedTask;
        var task = RunAsync();
        _reads.Track(task);
        return task;
        async Task RunAsync()
        {
            try { await read(); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception error) when (error is SqliteException or IOException or InvalidOperationException
                or HttpRequestException or MetadataServiceException)
            { if (ReferenceEquals(lifetime, _pageLifetime)) ReportReadFailure(error); }
        }
    }

    private void ReportReadFailure(Exception error)
    {
        ++_readFailures;
        _logger.LogWarning(error, "Archive read failed");
        if (_pageLifetime is not null) ShowStatus("读取记录失败，按 F5 重试。", InfoBarSeverity.Warning);
    }

    private Task<bool> EnsureActivePanelAsync() => _pageLifetime is { } lifetime
        ? _panels.EnsureActiveAsync(double.IsNaN(ReviewYear.Value) ? DateTime.Today.Year : (int)ReviewYear.Value, lifetime.Token)
        : Task.FromResult(false);

    private void OnPanelPublished(ArchivePanelKind panel)
    {
        if (_pageLifetime is null) return;
        switch (panel)
        {
            case ArchivePanelKind.Archives: ApplyArchiveFilter(refreshDetails: true); break;
            case ArchivePanelKind.Statistics: RenderStatistics(_panels.Statistics!); break;
            case ArchivePanelKind.Review:
                var review = _panels.Review!;
                RenderReview(review.Year, review.Statistics, review.Covers, review.Moments, review.Screenshots); break;
            case ArchivePanelKind.Screenshots: ApplyScreenshotFilter(); break;
        }
    }

    private void ApplyArchiveFilter(bool refreshDetails = false)
    {
        if (_pageLifetime is null || ArchiveList is null) return;
        var previous = ArchiveList.SelectedItem as ArchiveListItem;
        var selectedId = previous?.Archive.AnimeId ?? _details.SelectedArchive?.Archive.AnimeId;
        AniMeido.Contracts.Models.AnimeTrackingStatus? status = StatusFilter.SelectedIndex switch
        {
            1 => AniMeido.Contracts.Models.AnimeTrackingStatus.Watching,
            2 => AniMeido.Contracts.Models.AnimeTrackingStatus.PlanToWatch,
            3 => AniMeido.Contracts.Models.AnimeTrackingStatus.NotInterested,
            4 => AniMeido.Contracts.Models.AnimeTrackingStatus.Following,
            5 => AniMeido.Contracts.Models.AnimeTrackingStatus.Completed,
            6 => AniMeido.Contracts.Models.AnimeTrackingStatus.Dropped, _ => null,
        };
        var filtered = ArchiveBrowse.FilterArchives(_panels.Archives, ArchiveFilter.Text.Trim(), status,
            double.IsNaN(MinimumRatingFilter.Value) ? null : MinimumRatingFilter.Value,
            double.IsNaN(ArchiveYearFilter.Value) ? null : (int?)ArchiveYearFilter.Value);
        if (ArchiveList.ItemsSource is not IReadOnlyList<ArchiveListItem> existing || !ArchiveRowsMatch(existing, filtered))
        {
            _suppressArchiveSelection = true;
            try
            {
                ReplaceArchiveListSource(ArchiveList, filtered);
                ArchiveList.SelectedItem = filtered.FirstOrDefault(a => a.Archive.AnimeId == selectedId)
                    ?? (selectedId is null ? filtered.FirstOrDefault() : null);
            }
            finally { _suppressArchiveSelection = false; }
        }
        ArchiveResultCount.Text = $"{filtered.Count} 部";
        ArchiveListEmptyState.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var selected = ArchiveList.SelectedItem as ArchiveListItem;
        if (refreshDetails || !ReferenceEquals(previous, selected))
            _archiveSelectionRead = ReadAsync(() => selected is not null && selected.Archive.AnimeId == selectedId
                ? _details.RefreshSelectionAsync(selected) : _details.SelectAsync(selected));
    }

    private static bool ArchiveRowsMatch(IReadOnlyList<ArchiveListItem> previous, IReadOnlyList<ArchiveListItem> next)
    {
        if (previous.Count != next.Count) return false;
        for (var i = 0; i < previous.Count; i++)
        {
            var a = previous[i]; var b = next[i];
            if (a.Archive != b.Archive || a.TrackingStatus != b.TrackingStatus || a.Unavailable != b.Unavailable
                || a.EntryCount != b.EntryCount || a.ScreenshotCount != b.ScreenshotCount || !a.Tags.SequenceEqual(b.Tags))
                return false;
        }
        return true;
    }

    private void OnArchiveSelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (!_suppressArchiveSelection) _ = ReadAsync(() => _details.SelectAsync(ArchiveList.SelectedItem as ArchiveListItem)); }
    private void OnArchiveFilterChanged(object sender, TextChangedEventArgs e) => ApplyArchiveFilter();
    private void OnArchiveOptionChanged(object sender, SelectionChangedEventArgs e) => ApplyArchiveFilter();
    private void OnArchiveNumberFilterChanged(NumberBox sender, NumberBoxValueChangedEventArgs e) => ApplyArchiveFilter();
    private void OnClearArchiveFiltersClick(object sender, RoutedEventArgs e)
    {
        ArchiveFilter.Text = ""; StatusFilter.SelectedIndex = 0;
        MinimumRatingFilter.Value = ArchiveYearFilter.Value = double.NaN; ApplyArchiveFilter();
    }
    public bool TryRefreshFromKeyboard(Action<string> showSuccess)
    {
        if (_pageLifetime is not { } lifetime || _dialog is not null) return false;
        var version = _navigationVersion;
        var readingGeneration = _archiveReadingGeneration;
        if (_manualRefresh.IsCompleted)
            _manualRefresh = ReadAsync(async () =>
            {
                await _operations.DrainAsync();
                if (!IsNavigationCurrent(version, lifetime)) return;
                var failures = _readFailures;
                var panel = _panels.ActivePanel;
                _panels.InvalidateAll();
                var load = EnsureActivePanelAsync();
                await Task.WhenAll(RefreshPendingAsync(), load);
                if (!await load) return;
                if (panel == ArchivePanelKind.Archives)
                    await _archiveSelectionRead;
                if (IsNavigationCurrent(version, lifetime) && panel == _panels.ActivePanel && failures == _readFailures)
                {
                    if (panel == ArchivePanelKind.Archives && readingGeneration == _archiveReadingGeneration)
                    {
                        ++_archiveReadingGeneration;
                        QueueArchiveTitleReset();
                    }
                    showSuccess("档案已刷新");
                }
            });
        return true;
    }
    private void OnSectionButtonClick(object sender, RoutedEventArgs e)
    { if (sender is FrameworkElement { Tag: string tag }) { ShowPanel(tag); _ = ReadAsync(async () => await EnsureActivePanelAsync()); } }
    private void OnReviewYearChanged(NumberBox sender, NumberBoxValueChangedEventArgs e)
    {
        if (_suppressReviewYearChanged || double.IsNaN(ReviewYear.Value)) return;
        _panels.Invalidate(ArchivePanelKind.Review); _ = ReadAsync(async () => await EnsureActivePanelAsync());
    }
    private void OnReviewMoreScreenshotsClick(object sender, RoutedEventArgs e)
    { ScreenshotYearFilter.Value = ReviewYear.Value; ShowPanel("screenshots"); _ = ReadAsync(async () => await EnsureActivePanelAsync()); }
    private void OnScreenshotUnassignedChanged(object sender, RoutedEventArgs e) { _desktopScreenshotPage = 0; ApplyScreenshotFilter(); }
    private void OnPreviousScreenshotPage(object sender, RoutedEventArgs e)
    { if (_desktopScreenshotPage > 0) { --_desktopScreenshotPage; ApplyScreenshotFilter(); } }
    private void OnNextScreenshotPage(object sender, RoutedEventArgs e)
    { if (_desktopScreenshotPage + 1 < _desktopScreenshotPageCount) { ++_desktopScreenshotPage; ApplyScreenshotFilter(); } }
}
