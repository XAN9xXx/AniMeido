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
using VirtualKey = Windows.System.VirtualKey;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace AniMeido.Plugin.Base.Views;

public sealed partial class PastSeasonPage : Page, INavigationAware, IDesktopLoadingPage
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
    private readonly HashSet<AnimeCard> _realizedCards = [];
    private ObservableCollection<PastSeasonEntry>? _observedEntries;
    private CancellationTokenSource? _loading;
    private IDisposable? _dropHost;
    private PastSeasonTarget _selected;
    private int _visit;
    private int _loadVersion;
    private int _flyoutYear;
    private int _posterColumns = 1;
    private double _posterWidth = AnimeCard.PosterWidth;
    private double _scrollOffset;
    private bool _active;
    private bool _syncingControls;
    private bool _layoutQueued;
    private bool _restoreScroll;
    private bool _scrollRestoreQueued;
    private readonly DispatcherQueueTimer _settleScrollTimer;
    private double? _settlingScrollOffset;
    private bool _seasonPending;
    private bool _holdingSeason;
    private readonly DesktopSectionMotion _sectionMotion = new();
    private bool? _pendingSeasonBackward;
    private bool? _detailed;
    private PaperCardTrip? _paperTrip;
    private readonly PaperCardMotion _paperMotion;
    private readonly DispatcherQueueTimer _paperReturnTimeout;

    public DesktopPageLoadSurface LoadSurface { get; }
    /// <summary>返回的纸面已就位，外壳可以直接显示不透明的番剧库。</summary>
    public bool IsPaperCardReturning => _paperMotion.Active;
    public PastSeasonViewModel ViewModel { get; }
    private IReadOnlyList<DesktopCatalogFormatChip> FormatChipSlots { get; } =
        [new(null, "全部"), .. PastSeasonBrowse.FormatOrder.Select(format =>
            new DesktopCatalogFormatChip(format, AnimeReleaseClassifier.GetMediaFormatFilterText(format)))];

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
        _paperMotion = new(PaperMotionLayer, RootGrid);
        _paperReturnTimeout = Timer(600, CancelPaperReturn);
        LoadSurface = new(CatalogScroller, SkeletonRepeater);
        _settleScrollTimer = Timer(50, FinishScrollRestore);
        CatalogScroller.AddHandler(PointerWheelChangedEvent, new PointerEventHandler(OnCatalogScrollInput), handledEventsToo: true);
        CatalogScroller.AddHandler(PointerPressedEvent, new PointerEventHandler(OnCatalogScrollInput), handledEventsToo: true);
        CatalogScroller.AddHandler(KeyDownEvent, new KeyEventHandler(OnCatalogScrollKey), handledEventsToo: true);
        _seasonTimer = Timer(250, () => _ = LoadAsync());
        _skeletonTimer = Timer(150, () =>
        {
            if (_active && !_holdingSeason && (_seasonPending || ViewModel.IsLoading))
                LoadSurface.SetPlaceholder(true);
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
            if (_active && _selected != target)
            {
                HoldVisibleSeason();
                _selected = target;
                UpdateSeasonHeading();
                return LoadAsync();
            }
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
        UpdateFormatChips();
        _restoreScroll = true;
        if (_paperTrip?.Detail is { } from && PaperCardTrip.Enabled)
        {
            _paperMotion.Hold(_paperTrip, from);
            _paperReturnTimeout.Start();
            DesktopPresentationPreferences.AppearanceChanged += OnPaperAppearanceChanged;
        }
        else _paperTrip = null;
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
        StopPaperReturn();
        ++_visit;
        _scrollRestoreQueued = false;
        _settleScrollTimer.Stop();
        _settlingScrollOffset = null;
        CancelLoad();
        _seasonTimer.Stop();
        _skeletonTimer.Stop();
        _seasonPending = false;
        _holdingSeason = false;
        _pendingSeasonBackward = null;
        SeasonSwitchHint.Visibility = Visibility.Collapsed;
        LoadSurface.Unload();
        if (ViewModel.LoadedAnime.Count == 0 || !ReferenceEquals(LoadSurface.ReadyTarget, CatalogReadyTarget())) LoadSurface.Reset();
        _dropHost?.Dispose();
        _dropHost = null;
        ViewModel.PropertyChanged -= OnViewModelChanged;
        DesktopPresentationPreferences.Changed -= OnPreferencesChanged;
        if (_observedEntries is { } entries) entries.CollectionChanged -= OnEntriesChanged;
        _observedEntries = null;
        StopFilterMotion();
        _realizedCards.Clear();
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
        var backward = _pendingSeasonBackward;
        _pendingSeasonBackward = null;
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
            if (Current())
            {
                _seasonPending = false;
                if (_holdingSeason)
                {
                    _holdingSeason = false;
                    // 不让上一季的虚拟行参与下一季的逐条替换，避免回收中的卡片继续显示旧绑定。
                    CatalogRows.ItemsSource = null;
                    _scrollOffset = 0;
                    _restoreScroll = true;
                    UpdateRows();
                }
                UpdateSeasonHeading();
                UpdateLoading();
                UpdateFormatChips();
                QueueLayout();
                if (backward is bool direction) _sectionMotion.Play(SeasonBody, direction);
            }
        }
    }

    private void SelectSeason(PastSeasonTarget target, bool delay)
    {
        SeasonFlyout.Hide();
        target = PastSeasonBrowse.Clamp(target, FirstSeason, LatestSeason());
        if (_selected == target && !_seasonPending && !ViewModel.IsLoading && ViewModel.LoadedAnime.Count > 0) return;
        if (target != _selected) _pendingSeasonBackward = (target.Year, target.Season).CompareTo((_selected.Year, _selected.Season)) < 0;
        HoldVisibleSeason();
        _selected = target;
        CancelLoad();
        _seasonPending = true;
        _scrollOffset = 0;
        _restoreScroll = true;
        UpdateSeasonHeading();
        UpdateLoading();
        _seasonTimer.Stop();
        if (delay) _seasonTimer.Start(); else _ = LoadAsync();
    }

    private void HoldVisibleSeason()
    {
        StopFilterMotion();
        // 短暂等待期间沿用已呈现的标题和卡片。下一季数据到齐后，同一轮更新两者。
        _holdingSeason |= CatalogRows.ItemsSource is System.Collections.ICollection { Count: > 0 } && CatalogRows.Opacity > 0;
        if (_holdingSeason) CatalogRows.IsHitTestVisible = false;
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
        SeasonSwitchHint.Visibility = _holdingSeason ? Visibility.Visible : Visibility.Collapsed;
        if (!_holdingSeason)
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
        if (args.PropertyName == nameof(PastSeasonViewModel.FormatChips)) UpdateFormatChips();
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
    private void OnPreferencesChanged(object? sender, EventArgs args)
    {
        StopFilterMotion();
        QueueLayout();
    }
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
        if (_holdingSeason || _seasonPending || ViewModel.Season != _selected) return;
        var width = CatalogScroller.ViewportWidth > 0 ? CatalogScroller.ViewportWidth : CatalogScroller.ActualWidth;
        if (width <= 0) return;
        // 空结果只隐藏画面，保留已测量的有效视口；折叠后首次重新显示可能一次实现整个季度。
        var hasRows = ViewModel.Entries.Count > 0;
        CatalogRows.Opacity = hasRows ? 1 : 0;
        CatalogRows.IsHitTestVisible = hasRows;
        var detailed = DesktopPresentationPreferences.UseDetailedCatalogCards;
        if (_detailed != detailed)
        {
            _restoreScroll = true;
            // 海报行在实现时会改变估算高度。Uno 的纵向锚定会在滚轮途中改写偏移，
            // 让这次滚动提前结束。海报模式由页面单独恢复导航位置。
            if (detailed) CatalogScroller.ClearValue(ScrollViewer.VerticalAnchorRatioProperty);
            else CatalogScroller.VerticalAnchorRatio = double.NaN;
            CatalogRows.ItemsSource = null;
            if (detailed) _posterRows.Clear();
            else _detailedRows.Clear();
            CatalogRows.ItemTemplate = (DataTemplate)Resources[detailed ? "DesktopDetailedRowTemplate" : "DesktopPosterRowTemplate"];
            _detailed = detailed;
        }
        if (detailed)
        {
            var twoColumns = width >= 640;
            DesktopCatalogRow.SyncRows(_detailedRows, ViewModel.Entries, twoColumns);
            CatalogRows.ItemsSource = _detailedRows;
            SkeletonLayout.MinItemWidth = twoColumns ? width / 2 : width;
            SkeletonLayout.MinItemHeight = 216;
        }
        else
        {
            _posterColumns = Math.Max(1, (int)Math.Floor((width + 16) / (AnimeCard.PosterWidth + 16)));
            _posterWidth = Math.Min(AnimeCard.PosterWidth, Math.Max(92, width - 16));
            // 异步模板尚无子控件时也给外层虚拟布局一个行高下限，避免从零开始不断实现离屏行。
            DesktopCatalogPosterRow.SyncRows(_posterRows, ViewModel.Entries, _posterColumns,
                Math.Round(_posterWidth * 1.5) + 20);
            CatalogRows.ItemsSource = _posterRows;
            foreach (var card in _realizedCards)
                if (card.DisplayMode != AnimeCardDisplayMode.Detailed) card.CardWidth = _posterWidth;
            SkeletonLayout.MinItemWidth = Math.Floor(width / _posterColumns);
            SkeletonLayout.MinItemHeight = Math.Round(_posterWidth * 1.5) + 90;
        }
    }

    private void OnCatalogCardLoading(FrameworkElement sender, object args)
    {
        var card = (AnimeCard)sender;
        _realizedCards.Add(card);
        if (card.DisplayMode != AnimeCardDisplayMode.Detailed) card.CardWidth = _posterWidth;
    }
    private void OnCatalogCardUnloaded(object sender, RoutedEventArgs args) => _realizedCards.Remove((AnimeCard)sender);

    private void OnCatalogLayoutUpdated(object? sender, object args)
    {
        TryStartPaperReturn();
        if (_active && LoadSurface.ReadyTarget is null && !_layoutQueued
            && !_seasonPending && !ViewModel.IsLoading && !_holdingSeason
            && CatalogReadyTarget() is { ActualWidth: > 0, ActualHeight: > 0, Visibility: Visibility.Visible } target)
        {
            LoadSurface.SetPlaceholder(false);
            LoadSurface.Present(target);
        }

        // 在新布局第一次显示前设好起始位移，避免卡片到位后再往回跳。
        if (_filterOrigins is not null && !_layoutQueued) AnimateFilterResult();
        if (!_active || !_restoreScroll || ViewModel.IsLoading || _seasonPending) return;
        if (_scrollOffset <= 0)
        {
            RestoreCatalogScroll();
            return;
        }
        if (_scrollRestoreQueued) return;
        var visit = _visit;
        // 不在首轮 LayoutUpdated 里恢复深处偏移：这轮的虚拟视口仍在发布旧锚点。
        _scrollRestoreQueued = DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (!_active || visit != _visit) return;
            _scrollRestoreQueued = false;
            if (_restoreScroll && !ViewModel.IsLoading && !_seasonPending) RestoreCatalogScroll();
        });
    }

    private void RestoreCatalogScroll()
    {
        if (_scrollOffset > 0)
        {
            // Uno 重挂虚拟列表会在首轮定位后校正估算行高。保留原偏移，稳定后最多补一次。
            _settlingScrollOffset = _scrollOffset;
            _settleScrollTimer.Start();
        }
        CatalogScroller.ChangeView(null, _scrollOffset, null, disableAnimation: true);
        _restoreScroll = false;
    }

    private void FinishScrollRestore()
    {
        if (!_active || _settlingScrollOffset is not { } offset) return;
        _settlingScrollOffset = null;
        if (_restoreScroll || ViewModel.IsLoading || _seasonPending) return;
        if (Math.Abs(CatalogScroller.VerticalOffset - offset) > 0.1)
            CatalogScroller.ChangeView(null, offset, null, disableAnimation: true);
        else TryStartPaperReturn();
    }

    private void OnCatalogScrollInput(object sender, PointerRoutedEventArgs args) => YieldScrollRestore();

    private void OnCatalogScrollKey(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key is VirtualKey.Up or VirtualKey.Down or VirtualKey.PageUp or VirtualKey.PageDown
            or VirtualKey.Home or VirtualKey.End)
            YieldScrollRestore();
    }

    private void YieldScrollRestore()
    {
        if (_paperTrip is not null || PaperMotionLayer.Children.Count != 0) CancelPaperReturn();
        StopFilterMotion();
        if (!_restoreScroll && _settlingScrollOffset is null) return;
        _restoreScroll = false;
        _settleScrollTimer.Stop();
        _settlingScrollOffset = null;
        _scrollOffset = CatalogScroller.VerticalOffset;
    }

    private void OnCatalogViewChanged(object sender, ScrollViewerViewChangedEventArgs args)
    {
        if (_active && IsLoaded && !_restoreScroll && _settlingScrollOffset is null)
            _scrollOffset = CatalogScroller.VerticalOffset;
    }

    private void ResetScrollIfResultsChanged(ObservableCollection<PastSeasonEntry> previous)
    {
        // 筛选和排序在作品及顺序不变时会复用原集合。
        if (ReferenceEquals(previous, ViewModel.Entries)) return;

        CaptureFilterOrigins();
        _settleScrollTimer.Stop();
        _settlingScrollOffset = null;
        _scrollOffset = 0;
        _restoreScroll = true;
        QueueLayout();
    }

    private FrameworkElement? CatalogReadyTarget()
    {
        if (ErrorInfoBar.IsOpen) return ErrorInfoBar;
        if (ViewModel.Season != _selected) return null;
        if (ViewModel.IsError) return CatalogFailure;
        if (ViewModel.HasNoResults) return CatalogEmpty;
        return ViewModel.Entries.Count > 0 && _realizedCards.Count > 0 ? CatalogScroller : null;
    }

    private void UpdateLoading()
    {
        _skeletonTimer.Stop();
        if (!_holdingSeason && (ViewModel.IsLoading || _seasonPending)) _skeletonTimer.Start();
        else if (_holdingSeason || LoadSurface.ReadyTarget is not null) LoadSurface.SetPlaceholder(false);
    }

    private void OnSortChanged(object sender, SelectionChangedEventArgs args)
    {
        if (SortComboBox.SelectedItem is not ComboBoxItem { Tag: PastSeasonSortKey key }) return;
        var previous = ViewModel.Entries;
        ViewModel.SetSortKey(key);
        UpdateSortDirection();
        ResetScrollIfResultsChanged(previous);
    }
    private void OnSortDirectionClick(object sender, RoutedEventArgs args)
    {
        var previous = ViewModel.Entries;
        ViewModel.ToggleSortDirection();
        UpdateSortDirection();
        ResetScrollIfResultsChanged(previous);
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
        var previous = ViewModel.Entries;
        ViewModel.SelectFormat(chip.Format);
        UpdateFormatChips();
        ResetScrollIfResultsChanged(previous);
    }

    private void UpdateFormatChips()
    {
        // 过渡期间保留上一批计数和位置，只响应用户选中的形态。新季度到齐后原地更新计数。
        var loading = _holdingSeason || _seasonPending || ViewModel.IsLoading;
        foreach (var slot in FormatChipSlots)
        {
            var chip = slot.Chip;
            var count = loading ? chip.Count
                : ViewModel.FormatChips.FirstOrDefault(item => item.Format == chip.Format)?.Count ?? 0;
            slot.Chip = chip with { Count = count, IsSelected = ViewModel.SelectedFormat == chip.Format };
        }
    }
    private void OnHideCompletedToggled(object sender, RoutedEventArgs args)
    {
        if (_syncingControls) return;
        var previous = ViewModel.Entries;
        ViewModel.SetHideCompleted(HideCompletedSwitch.IsOn);
        ResetScrollIfResultsChanged(previous);
    }
    private void OnFilterTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (_syncingControls) return;
        var previous = ViewModel.Entries;
        ViewModel.SetQuery(sender.Text);
        ResetScrollIfResultsChanged(previous);
    }
    private void OnClearFiltersClick(object sender, RoutedEventArgs args)
    {
        _syncingControls = true;
        FilterBox.Text = string.Empty;
        HideCompletedSwitch.IsOn = false;
        _syncingControls = false;
        var previous = ViewModel.Entries;
        ViewModel.ClearFilters();
        ResetScrollIfResultsChanged(previous);
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
    {
        CancelPaperReturn();
        if (PaperCardTrip.Enabled && sender is AnimeCard card && card.MeasurePaperCard(PaperMotionLayer) is { } origin)
            _paperTrip = new(args.Anime.ID, args.Anime.Title, card.DisplayedCover, origin);
        _navigator.Navigate(typeof(AnimeDetailPage), (object?)_paperTrip ?? args.Anime.ID);
    }

    private void OnPaperAppearanceChanged(object? sender, EventArgs args) => CancelPaperReturn();

    private void TryStartPaperReturn()
    {
        if (!_active || _paperTrip is not { Detail: { } from } trip || !_paperMotion.Waiting
            || _layoutQueued || _restoreScroll || _settlingScrollOffset is not null) return;
        var card = _realizedCards.FirstOrDefault(c => c.Anime?.ID == trip.Id && ViewportPosition(c) is not null);
        if (card?.MeasurePaperCard(PaperMotionLayer) is not { } to) return;
        _paperReturnTimeout.Stop();
        _paperTrip = null;
        _paperMotion.Play(from, to);
    }

    private void CancelPaperReturn()
    {
        _paperTrip = null;
        StopPaperReturn();
    }

    private void StopPaperReturn()
    {
        _paperReturnTimeout.Stop();
        _paperMotion.Stop();
        DesktopPresentationPreferences.AppearanceChanged -= OnPaperAppearanceChanged;
    }
}
