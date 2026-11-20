using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using AniMeido.Plugin.Base.Views.Controls;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace AniMeido.Plugin.Base.Views;

public sealed partial class PastSeasonPage : Page, INavigationAware
{
    private static readonly PastSeasonTarget FirstSeason = new(1900, Season.Winter);
    private readonly CacheService _cache;
    private readonly DragDropService _dragDrop;
    private readonly IPluginNavigator _navigator;
    private readonly ILogger<PastSeasonPage> _logger;
    private readonly DispatcherQueueTimer _seasonTimer;
    private readonly DispatcherQueueTimer _skeletonTimer;
    private readonly ObservableCollection<DesktopCatalogRow> _detailedRows = [];
    private readonly ObservableCollection<DesktopCatalogPosterRow> _posterRows = [];
    private readonly HashSet<AnimeCard> _visiblePosters = [];
    private ObservableCollection<PastSeasonEntry>? _observedEntries;
    private CancellationTokenSource? _loading;
    private IDisposable? _dropHost;
    private PastSeasonTarget _selected;
    private int _visit;
    private int _loadVersion;
    private int _flyoutYear;
    private int _posterColumns = 1;
    private double _posterWidth = 150;
    private double _scrollOffset;
    private bool _active;
    private bool _syncingControls;
    private bool _layoutQueued;
    private bool _restoreScroll;
    private bool _seasonPending;
    private bool? _detailed;

    public PastSeasonViewModel ViewModel { get; }

    public PastSeasonPage(IAnimeDataSource dataSource, CacheService cacheService, DragDropService dragDropService,
        TrackingService trackingService, IPluginNavigator pluginNavigator, ILogger<PastSeasonPage> logger)
    {
        ViewModel = new PastSeasonViewModel(dataSource, trackingService, logger);
        _cache = cacheService;
        _dragDrop = dragDropService;
        _navigator = pluginNavigator;
        _logger = logger;
        _selected = LatestSeason();
        InitializeComponent();
        _seasonTimer = Timer(250, () => _ = LoadAsync());
        _skeletonTimer = Timer(150, () =>
        {
            if (_active && (_seasonPending || ViewModel.IsLoading))
                SkeletonRepeater.Visibility = Visibility.Visible;
        });
        SkeletonRepeater.ItemsSource = Enumerable.Range(0, 12).ToArray();
        foreach (var (key, label) in new[] { (PastSeasonSortKey.Score, "评分"), (PastSeasonSortKey.AirDate, "开播日期"), (PastSeasonSortKey.Title, "标题") })
            SortComboBox.Items.Add(new ComboBoxItem { Content = label, Tag = key });
        SortComboBox.SelectedIndex = 0;
        SortComboBox.SelectionChanged += OnSortChanged;
        UpdateSeasonHeading();
        UpdateSortDirection();
    }

    private DispatcherQueueTimer Timer(int milliseconds, Action action)
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(milliseconds);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => action();
        return timer;
    }

    public Task OnNavigatedToAsync(object? parameter)
    {
        if (parameter is PastSeasonTarget target && PastSeasonBrowse.IsWithin(target, FirstSeason, LatestSeason()))
        {
            _selected = target;
            UpdateSeasonHeading();
        }
        return _active && NeedsSeasonLoad() ? LoadAsync() : Task.CompletedTask;
    }

    private bool NeedsSeasonLoad() => !ViewModel.IsLoading
        && (ViewModel.Season != _selected || ViewModel.LoadedAnime.Count == 0);

    internal static (int Year, Season Season) GetLatestCompletedSeason(DateTime now)
        => now.Month switch
        {
            <= 3 => (now.Year - 1, Season.Fall),
            <= 6 => (now.Year, Season.Winter),
            <= 9 => (now.Year, Season.Spring),
            _ => (now.Year, Season.Summer),
        };

    private static PastSeasonTarget LatestSeason()
    {
        var (year, season) = GetLatestCompletedSeason(DateTime.Now);
        return new(year, season);
    }

    private void OnPageLoaded(object sender, RoutedEventArgs args)
    {
        if (_active) return;
        _active = true;
        var visit = ++_visit;
        _dropHost = _dragDrop.AttachStandardDragHost(RootGrid, DragOverlay, DragAction.PlanToWatch);
        ViewModel.PropertyChanged += OnViewModelChanged;
        DesktopPresentationPreferences.Changed += OnPreferencesChanged;
        ObserveEntries();
        _restoreScroll = true;
        QueueLayout();
        _ = ReloadPersonalStateAsync(visit);
        // 导航参数先在本次调度中落定，再加载默认季度。
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (_active && _visit == visit && NeedsSeasonLoad()) _ = LoadAsync();
        });
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs args)
    {
        if (!_active) return;
        _active = false;
        ++_visit;
        CancelLoad();
        _seasonTimer.Stop();
        _skeletonTimer.Stop();
        _seasonPending = false;
        SkeletonRepeater.Visibility = Visibility.Collapsed;
        _dropHost?.Dispose();
        _dropHost = null;
        ViewModel.PropertyChanged -= OnViewModelChanged;
        DesktopPresentationPreferences.Changed -= OnPreferencesChanged;
        if (_observedEntries is { } entries) entries.CollectionChanged -= OnEntriesChanged;
        _observedEntries = null;
        _visiblePosters.Clear();
        SeasonFlyout.Hide();
    }

    private async Task ReloadPersonalStateAsync(int visit)
    {
        try
        {
            await _dragDrop.ReloadConfigAsync();
            if (_active && _visit == visit) await ViewModel.ReloadStatusesAsync();
        }
#pragma warning disable CA1031 // 页面接纳的配置读取在这里记录，保留已有列表。
        catch (Exception error)
        {
            if (_active && _visit == visit)
                _logger.LogWarning(error, "Library personal state could not be refreshed");
        }
#pragma warning restore CA1031
    }

    private void CancelLoad()
    {
        ++_loadVersion;
        _loading?.Cancel();
        _loading = null;
        ViewModel.Suspend();
    }

    private async Task LoadAsync(bool refresh = false)
    {
        if (!_active) return;
        _seasonTimer.Stop();
        CancelLoad();
        var target = _selected;
        var version = _loadVersion;
        using var cancellation = new CancellationTokenSource();
        _loading = cancellation;
        bool Current() => _active && version == _loadVersion && !cancellation.IsCancellationRequested;
        try
        {
            _seasonPending = refresh;
            UpdateLoading();
            ErrorInfoBar.IsOpen = false;
            if (ViewModel.Season != target)
            {
                _syncingControls = true;
                FilterBox.Text = string.Empty;
                _syncingControls = false;
                _scrollOffset = 0;
            }
            if (refresh)
            {
                var key = await MetadataDataSource.GetSeasonCacheKeyAsync(_cache, target.Year, target.Season);
                if (!Current()) return;
                await _cache.RemoveCacheAsync(key);
                if (!Current()) return;
            }
            _seasonPending = false;
            await ViewModel.LoadPastSeasonAnimeAsync(target.Year, target.Season, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
#pragma warning disable CA1031 // 页面加载入口统一记录失败，并保持工具栏可用。
        catch (Exception error)
        {
            if (!Current()) return;
            _logger.LogError(error, "Library season {Year}/{Season} failed to load", target.Year, target.Season);
            ErrorInfoBar.Message = "番剧库加载失败，请重试。";
            ErrorInfoBar.IsOpen = true;
        }
#pragma warning restore CA1031
        finally
        {
            if (ReferenceEquals(_loading, cancellation)) _loading = null;
            if (Current()) { _seasonPending = false; UpdateSeasonHeading(); UpdateLoading(); QueueLayout(); }
        }
    }

    private void SelectSeason(PastSeasonTarget target, bool delay)
    {
        SeasonFlyout.Hide();
        _selected = PastSeasonBrowse.Clamp(target, FirstSeason, LatestSeason());
        CancelLoad();
        _seasonPending = true;
        _scrollOffset = 0;
        _restoreScroll = true;
        CatalogRows.ItemsSource = null;
        UpdateSeasonHeading();
        UpdateLoading();
        _seasonTimer.Stop();
        if (delay) _seasonTimer.Start(); else _ = LoadAsync();
    }

    private void StepSeason(int step)
    {
        if (PastSeasonBrowse.Step(_selected, step, FirstSeason, LatestSeason()) is { } target)
            SelectSeason(target, delay: true);
    }

    private void UpdateSeasonHeading()
    {
        var latest = LatestSeason();
        SeasonLabel.Text = $"{_selected.Year} {PastSeasonBrowse.SeasonName(_selected.Season)}";
        SeasonMonthsLabel.Text = PastSeasonBrowse.SeasonMonths(_selected.Season);
        SeasonSummary.Text = !_seasonPending && ViewModel.Season == _selected
            ? ViewModel.SummaryText : $"{_selected.Year} 年{PastSeasonBrowse.SeasonName(_selected.Season)}季";
        PrevSeasonButton.IsEnabled = PastSeasonBrowse.Step(_selected, -1, FirstSeason, latest) is not null;
        NextSeasonButton.IsEnabled = PastSeasonBrowse.Step(_selected, 1, FirstSeason, latest) is not null;
    }

    private void OnPrevSeasonClick(object sender, RoutedEventArgs args) => StepSeason(-1);
    private void OnNextSeasonClick(object sender, RoutedEventArgs args) => StepSeason(1);
    private void OnLatestSeasonClick(object sender, RoutedEventArgs args) => SelectSeason(LatestSeason(), false);
    private void OnRetryClick(object sender, RoutedEventArgs args) => _ = LoadAsync();
    private void OnRefreshInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        _ = LoadAsync(refresh: true);
    }

    private void OnSeasonFlyoutOpening(object sender, object args) => ShowFlyoutYear(_selected.Year);
    private void OnFlyoutPrevYearClick(object sender, RoutedEventArgs args) => ShowFlyoutYear(_flyoutYear - 1);
    private void OnFlyoutNextYearClick(object sender, RoutedEventArgs args) => ShowFlyoutYear(_flyoutYear + 1);
    private void OnFlyoutYearChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_syncingControls && double.IsFinite(args.NewValue)) ShowFlyoutYear((int)Math.Round(args.NewValue));
    }

    private void ShowFlyoutYear(int year)
    {
        var latest = LatestSeason();
        _flyoutYear = Math.Clamp(year, FirstSeason.Year, latest.Year);
        _syncingControls = true;
        FlyoutYearBox.Minimum = FirstSeason.Year;
        FlyoutYearBox.Maximum = latest.Year;
        FlyoutYearBox.Value = _flyoutYear;
        _syncingControls = false;
        FlyoutPrevYearButton.IsEnabled = _flyoutYear > FirstSeason.Year;
        FlyoutNextYearButton.IsEnabled = _flyoutYear < latest.Year;
        FlyoutSeasonGrid.Children.Clear();
        foreach (var season in new[] { Season.Winter, Season.Spring, Season.Summer, Season.Fall })
        {
            var target = new PastSeasonTarget(_flyoutYear, season);
            var button = new ToggleButton
            {
                Content = PastSeasonBrowse.SeasonName(season), IsChecked = target == _selected,
                HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(0, 8, 0, 8),
                IsEnabled = PastSeasonBrowse.IsWithin(target, FirstSeason, latest),
            };
            button.Click += (_, _) => SelectSeason(target, false);
            Grid.SetColumn(button, (int)season - 1);
            FlyoutSeasonGrid.Children.Add(button);
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(PastSeasonViewModel.Entries)) { ObserveEntries(); QueueLayout(); }
        if (args.PropertyName == nameof(PastSeasonViewModel.SummaryText)) UpdateSeasonHeading();
        if (args.PropertyName is nameof(PastSeasonViewModel.IsLoading) or nameof(PastSeasonViewModel.IsError)) UpdateLoading();
    }

    private void ObserveEntries()
    {
        if (ReferenceEquals(_observedEntries, ViewModel.Entries)) return;
        if (_observedEntries is { } previous) previous.CollectionChanged -= OnEntriesChanged;
        _observedEntries = ViewModel.Entries;
        _observedEntries.CollectionChanged += OnEntriesChanged;
    }

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs args) => QueueLayout();
    private void OnPreferencesChanged(object? sender, EventArgs args) => QueueLayout();
    private void OnCatalogSizeChanged(object sender, SizeChangedEventArgs args) => QueueLayout();

    private void QueueLayout()
    {
        if (!_active || _layoutQueued) return;
        _layoutQueued = true;
        if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _layoutQueued = false;
            if (_active) UpdateRows();
        })) _layoutQueued = false;
    }

    private void UpdateRows()
    {
        if (_seasonPending || ViewModel.Season != _selected) return;
        var width = CatalogScroller.ViewportWidth > 0 ? CatalogScroller.ViewportWidth : CatalogScroller.ActualWidth;
        if (width <= 0) return;
        var detailed = DesktopPresentationPreferences.UseDetailedCatalogCards;
        if (_detailed != detailed)
        {
            _restoreScroll = true;
            CatalogRows.ItemsSource = null;
            CatalogRows.ItemTemplate = (DataTemplate)Resources[detailed ? "DesktopDetailedRowTemplate" : "DesktopPosterRowTemplate"];
            _detailed = detailed;
        }
        if (detailed)
        {
            var twoColumns = width >= 640;
            DesktopCatalogRow.SyncRows(_detailedRows, ViewModel.Entries, twoColumns);
            CatalogRows.ItemsSource = _detailedRows;
            SkeletonLayout.MinItemWidth = twoColumns ? width / 2 : width;
            SkeletonLayout.MinItemHeight = 330;
        }
        else
        {
            _posterColumns = Math.Max(1, (int)Math.Floor((width + 17) / 153));
            _posterWidth = Math.Max(92, Math.Floor(width / _posterColumns) - 17);
            DesktopCatalogPosterRow.SyncRows(_posterRows, ViewModel.Entries, _posterColumns);
            CatalogRows.ItemsSource = _posterRows;
            foreach (var card in _visiblePosters) card.CardWidth = _posterWidth;
            SkeletonLayout.MinItemWidth = Math.Floor(width / _posterColumns);
            SkeletonLayout.MinItemHeight = Math.Round(_posterWidth * 1.5) + 90;
        }
    }

    private void OnPosterLoaded(object sender, RoutedEventArgs args)
    {
        var card = (AnimeCard)sender;
        _visiblePosters.Add(card);
        card.CardWidth = _posterWidth;
    }
    private void OnPosterUnloaded(object sender, RoutedEventArgs args) => _visiblePosters.Remove((AnimeCard)sender);

    private void OnCatalogLayoutUpdated(object? sender, object args)
    {
        if (!_active || !_restoreScroll || ViewModel.IsLoading || _seasonPending) return;
        _restoreScroll = false;
        CatalogScroller.ChangeView(null, _scrollOffset, null, disableAnimation: true);
    }
    private void OnCatalogViewChanged(object sender, ScrollViewerViewChangedEventArgs args)
    {
        if (_active && IsLoaded && !_restoreScroll) _scrollOffset = CatalogScroller.VerticalOffset;
    }

    private void ResetScroll()
    {
        _scrollOffset = 0;
        _restoreScroll = true;
        QueueLayout();
    }

    private void UpdateLoading()
    {
        _skeletonTimer.Stop();
        if (ViewModel.IsLoading || _seasonPending) _skeletonTimer.Start();
        else SkeletonRepeater.Visibility = Visibility.Collapsed;
    }

    private void OnSortChanged(object sender, SelectionChangedEventArgs args)
    {
        if (SortComboBox.SelectedItem is not ComboBoxItem { Tag: PastSeasonSortKey key }) return;
        ViewModel.SetSortKey(key);
        UpdateSortDirection();
        ResetScroll();
    }
    private void OnSortDirectionClick(object sender, RoutedEventArgs args)
    {
        ViewModel.ToggleSortDirection();
        UpdateSortDirection();
        ResetScroll();
    }
    private void UpdateSortDirection()
    {
        SortDirectionIcon.Glyph = ViewModel.SortAscending ? "\uE74A" : "\uE74B";
        var label = PastSeasonBrowse.SortDirectionText(ViewModel.SortKey, ViewModel.SortAscending);
        AutomationProperties.SetName(SortDirectionButton, label);
    }
    private void OnFormatChipClick(object sender, RoutedEventArgs args)
    {
        if (sender is not ToggleButton { Tag: PastSeasonFormatChip chip } button) return;
        button.IsChecked = true;
        if (chip.IsSelected) return;
        ViewModel.SelectFormat(chip.Format);
        ResetScroll();
    }
    private void OnHideCompletedToggled(object sender, RoutedEventArgs args)
    {
        if (_syncingControls) return;
        ViewModel.SetHideCompleted(HideCompletedSwitch.IsOn);
        ResetScroll();
    }
    private void OnFilterTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (_syncingControls) return;
        ViewModel.SetQuery(sender.Text);
        ResetScroll();
    }
    private void OnClearFiltersClick(object sender, RoutedEventArgs args)
    {
        _syncingControls = true;
        FilterBox.Text = string.Empty;
        HideCompletedSwitch.IsOn = false;
        _syncingControls = false;
        ViewModel.ClearFilters();
        ResetScroll();
    }
    private void OnFindAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        FilterBox.Focus(FocusState.Keyboard);
        args.Handled = true;
    }
    private void OnRootKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (XamlRoot is { } root && FocusManager.GetFocusedElement(root) is TextBox or PasswordBox or RichEditBox or NumberBox) return;
        if ((int)args.Key is not (219 or 221)) return;
        StepSeason((int)args.Key == 219 ? -1 : 1);
        args.Handled = true;
    }
    private void OnAnimeCardClicked(object? sender, AnimeCardClickedEventArgs args)
        => _navigator.Navigate(typeof(AnimeDetailPage), args.Anime.ID);
}
