using System.ComponentModel;
using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using AniMeido.Plugin.Base.Views.Controls;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace AniMeido.Plugin.Base.Views;

public sealed partial class CurrentSeasonPage : Page, IDesktopLoadingPage
{
    private readonly DragDropService _dragDrop;
    private readonly IPluginNavigator _navigator;
    private readonly ILogger<CurrentSeasonPage> _logger;
    private readonly HashSet<FrameworkElement> _hoveredRows = [];
    private IDisposable? _dropHost;
    private long _dropVisibilityCallback;
    private int _visit;
    private bool _active;
    private bool _refreshing;
    private DesktopCalendarLayout? _discoveryLayout;
    private bool _hasPresentedBody;
    private bool _awaitingInitialLayout;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _initialHintTimer;
    private bool _initialHintDue;

    public DesktopPageLoadSurface LoadSurface { get; }
    public CurrentSeasonViewModel ViewModel { get; }
    internal Task PendingActivation { get; private set; } = Task.CompletedTask;

    public CurrentSeasonPage(IAnimeDataSource dataSource, DragDropService dragDropService,
        TrackingService trackingService, PersonalFeatureStateStore featureState,
        IPluginNavigator pluginNavigator, ILogger<CurrentSeasonPage> logger)
    {
        ViewModel = new(dataSource, trackingService, featureState, logger);
        _dragDrop = dragDropService;
        _navigator = pluginNavigator;
        _logger = logger;
        InitializeComponent();
        _initialHintTimer = DispatcherQueue.CreateTimer();
        _initialHintTimer.Interval = TimeSpan.FromMilliseconds(150);
        _initialHintTimer.IsRepeating = false;
        _initialHintTimer.Tick += (_, _) =>
        {
            if (!_active) return;
            _initialHintDue = true;
            UpdateLoading();
        };
        LoadSurface = new(BodyScroller, LoadingOverlay, LoadingRing);
        DiscoverHost.PreparingLayout = PrepareDiscoveryLayout;
        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
    }

    private void OnPageLoaded(object sender, RoutedEventArgs args)
    {
        if (_active) return;
        _active = true;
        var visit = ++_visit;
        ViewModel.PropertyChanged += OnViewModelChanged;
        DesktopPresentationPreferences.AppearanceChanged += OnAppearanceChanged;
        _dropVisibilityCallback = DragOverlay.RegisterPropertyChangedCallback(VisibilityProperty, OnDropVisibilityChanged);
        _dropHost = _dragDrop.AttachStandardDragHost(RootGrid, DragOverlay, DragAction.PlanToWatch);
        UpdateControls();
        PendingActivation = ActivateAsync(visit);
    }

    private async Task ActivateAsync(int visit)
    {
        try
        {
            ViewModel.ResumeSupplementaryLoads();
            await ViewModel.ResumeMainLoadCommand.ExecuteAsync(null);
            await (ViewModel.LoadSeasonalAnimeCommand.ExecutionTask ?? Task.CompletedTask);
            if (!IsCurrent(visit)) return;
            PresentBodyWhenArranged();
            await _dragDrop.ReloadConfigAsync();
            if (IsCurrent(visit)) await ViewModel.ReloadStatusesAsync();
        }
        catch (OperationCanceledException) when (!IsCurrent(visit)) { }
#pragma warning disable CA1031 // 页面读取入口统一收口未由 ViewModel 处理的失败。
        catch (Exception error)
        {
            _logger.LogError(error, "放送日历页面读取失败。");
            if (IsCurrent(visit)) ShowFailure("放送日历暂时无法读取，请按 F5 重试。");
        }
#pragma warning restore CA1031
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs args)
    {
        if (!_active) return;
        _active = false;
        _initialHintTimer.Stop();
        _initialHintDue = false;
        DesktopPresentationPreferences.AppearanceChanged -= OnAppearanceChanged;
        ++_visit;
        ViewModel.PropertyChanged -= OnViewModelChanged;
        DragOverlay.UnregisterPropertyChangedCallback(VisibilityProperty, _dropVisibilityCallback);
        ViewModel.LoadSeasonalAnimeCommand.Cancel();
        ViewModel.CancelSupplementaryLoads();
        StopWaitingForInitialLayout();
        LoadSurface.Unload();
        _dropHost?.Dispose();
        _dropHost = null;
        _hoveredRows.Clear();
        ManagedImageLoader.Cancel(DailyPickCover);
    }

    private bool IsCurrent(int visit) => _active && visit == _visit;

    internal async Task RefreshAsync()
    {
        if (!_active || _refreshing) return;
        var visit = _visit;
        _refreshing = true;
        if (ViewModel.IsError) LoadSurface.Reset();
        try
        {
            ViewModel.LoadSeasonalAnimeCommand.Cancel();
            ViewModel.CancelSupplementaryLoads();
            await (ViewModel.LoadSeasonalAnimeCommand.ExecutionTask ?? Task.CompletedTask);
            if (!IsCurrent(visit)) return;
            ViewModel.ResumeSupplementaryLoads();
            await ViewModel.LoadSeasonalAnimeCommand.ExecuteAsync(null);
            if (IsCurrent(visit)) PresentBodyWhenArranged();
        }
        catch (OperationCanceledException) when (!IsCurrent(visit)) { }
#pragma warning disable CA1031 // 刷新入口负责显示未知读取失败，已知远端错误由 ViewModel 映射。
        catch (Exception error)
        {
            _logger.LogError(error, "放送日历刷新失败。");
            if (IsCurrent(visit)) ShowFailure("刷新失败，请稍后重试。");
        }
#pragma warning restore CA1031
        finally { _refreshing = false; }
    }

    private async void OnRefreshInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await RefreshAsync();
    }

    private async void OnLoadingOverlayTapped(object sender, TappedRoutedEventArgs args)
    {
        if (ViewModel.IsError) await RefreshAsync();
    }

    private async void OnDropVisibilityChanged(DependencyObject sender, DependencyProperty property)
    {
        if (!_active || DragOverlay.Visibility != Visibility.Collapsed) return;
        var visit = _visit;
        try
        {
            await _dragDrop.ReloadConfigAsync();
            if (IsCurrent(visit)) await ViewModel.ReloadStatusesAsync();
        }
#pragma warning disable CA1031 // 拖放结束后的界面刷新与已经提交的标记分别报告。
        catch (Exception error)
        {
            _logger.LogError(error, "放送日历标记刷新失败。");
            if (IsCurrent(visit)) ShowFailure("标记显示暂时未更新，请按 F5 刷新。");
        }
#pragma warning restore CA1031
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        switch (args.PropertyName)
        {
            case nameof(CurrentSeasonViewModel.IsLoading):
            case nameof(CurrentSeasonViewModel.IsError):
            case nameof(CurrentSeasonViewModel.ErrorMessage):
                UpdateLoading();
                break;
            case nameof(CurrentSeasonViewModel.IsFiltering):
                UpdateResultSurface();
                break;
            case nameof(CurrentSeasonViewModel.SearchText):
                if (FilterBox.Text != ViewModel.SearchText) FilterBox.Text = ViewModel.SearchText;
                break;
            case nameof(CurrentSeasonViewModel.ShowMineOnly):
                UpdateScope();
                break;
            case nameof(CurrentSeasonViewModel.VisibleEntries):
                ShelfScroller.ChangeView(0, null, null, disableAnimation: true);
                ResultsScroller.ChangeView(null, 0, null, disableAnimation: true);
                break;
            case nameof(CurrentSeasonViewModel.TimeMachineYearsAgo):
                UpdateTimeMachineYears();
                break;
            case nameof(CurrentSeasonViewModel.DailyPick):
                UpdateDailyPickCover();
                DiscoverHost.InvalidateMeasure();
                break;
            case nameof(CurrentSeasonViewModel.IsDailyPickWatching):
                UpdateDailyPickAction();
                break;
        }
    }

    private void UpdateControls()
    {
        FilterBox.Text = ViewModel.SearchText;
        SortBox.SelectedIndex = (int)ViewModel.Sort;
        UpdateScope();
        UpdateTimeMachineYears();
        UpdateLoading();
        UpdateResultSurface();
        UpdateDailyPickCover();
        UpdateDailyPickAction();
    }

    private void OnAppearanceChanged(object? sender, EventArgs args) => UpdateLoading();

    private void UpdateLoading()
    {
        var loading = !ViewModel.IsError && (ViewModel.IsLoading || !_hasPresentedBody);
        LoadingFailedImage.Visibility = ViewModel.IsError ? Visibility.Visible : Visibility.Collapsed;
        LoadingHint.Text = ViewModel.IsError ? $"{ViewModel.ErrorMessage}\n点击重试" : "正在读取…";
        var resources = Application.Current.Resources;
        var paper = resources["DesktopUseSegmentedChoices"] is true;
        // 首次排版也会短暂进入 loading。暖纸保留页面底色，只有确实等了一会才显示提示。
        var defer = paper && loading && !_hasPresentedBody && !_initialHintDue;
        if (defer && !_initialHintTimer.IsRunning) _initialHintTimer.Start();
        if (!defer) _initialHintTimer.Stop();
        if (!loading) _initialHintDue = false;
        LoadingOverlay.Background = (Brush)resources[paper && !ViewModel.IsError ? "DesktopPageBrush" : "DesktopFailedOverlayBrush"];
        LoadSurface.SetPlaceholder(loading && !defer || ViewModel.IsError, error: ViewModel.IsError);
        if (ViewModel.IsError) LoadSurface.Present(LoadingMessage);
        else if (!loading) LoadSurface.Present();
    }

    private void PresentBodyWhenArranged()
    {
        if (_hasPresentedBody || _awaitingInitialLayout || ViewModel.IsLoading || ViewModel.IsError) return;
        // 正文仍正常测量；等最终行列落位再一起显示，避免先画出空列表和横跨两列的时光机。
        _awaitingInitialLayout = true;
        BodyScroller.LayoutUpdated += OnInitialBodyLayout;
        BodyScroller.InvalidateMeasure();
    }

    private void OnInitialBodyLayout(object? sender, object args)
    {
        if (!_active || ViewModel.IsLoading || ViewModel.IsError) return;
        if (BodyScroller.ActualWidth <= 0 || BodyScroller.ActualHeight <= 0) return;
        if (ViewModel.IsFiltering ? ResultsScroller.ActualHeight <= 0 : _discoveryLayout is null) return;
        StopWaitingForInitialLayout();
        _hasPresentedBody = true;
        BodyScroller.Opacity = 1;
        BodyScroller.IsEnabled = true;
        UpdateLoading();
    }

    private void StopWaitingForInitialLayout()
    {
        if (!_awaitingInitialLayout) return;
        _awaitingInitialLayout = false;
        BodyScroller.LayoutUpdated -= OnInitialBodyLayout;
    }

    private void UpdateResultSurface()
    {
        var filtering = ViewModel.IsFiltering;
        CalendarBodyGrid.RowDefinitions[0].Height = filtering ? new(1, GridUnitType.Star) : GridLength.Auto;
        CalendarBodyGrid.RowDefinitions[1].Height = GridLength.Auto;
        DiscoverHost.Visibility = ShelfHost.Visibility = filtering ? Visibility.Collapsed : Visibility.Visible;
        ResultsScroller.Visibility = filtering ? Visibility.Visible : Visibility.Collapsed;
        BodyScroller.VerticalScrollMode = filtering ? ScrollMode.Disabled : ScrollMode.Enabled;
        BodyScroller.VerticalScrollBarVisibility = filtering ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        BodyScroller.ChangeView(null, 0, null, disableAnimation: true);
        UpdateBodyHeight();
        if (filtering) ViewModel.SetDiscoverCapacity(0);
        else
        {
            _discoveryLayout = null;
            DiscoverHost.InvalidateMeasure();
        }
    }

    private void OnLayoutSizeChanged(object sender, SizeChangedEventArgs args) => UpdateBodyHeight();

    private void UpdateBodyHeight()
        => CalendarBodyGrid.Height = ViewModel.IsFiltering ? BodyScroller.ActualHeight : double.NaN;

    private void PrepareDiscoveryLayout(double width)
    {
        if (ViewModel.IsFiltering) return;
        var layout = DesktopCalendarLayout.Calculate(width, ViewModel.HasDailyPick);
        if (_discoveryLayout == layout) return;
        _discoveryLayout = layout;
        DiscoverHost.RowDefinitions[0].Height = new(DesktopCalendarLayout.PanelHeight);
        DiscoverHost.RowDefinitions[1].Height = new(layout.StackPanels ? DesktopCalendarLayout.PanelHeight : 0);
        DiscoverHost.ColumnDefinitions[0].Width = layout.StackPanels ? new(1, GridUnitType.Star) : new(layout.DailyPickWidth);
        DiscoverHost.ColumnDefinitions[1].Width = new(layout.StackPanels ? 0 : 1, GridUnitType.Star);
        DiscoverHost.RowSpacing = layout.StackPanels ? DesktopCalendarLayout.PanelSpacing : 0;
        DiscoverHost.ColumnSpacing = !layout.StackPanels && ViewModel.HasDailyPick ? DesktopCalendarLayout.PanelSpacing : 0;
        DailyPickPanel.Visibility = ViewModel.HasDailyPick ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetRow(DiscoverPanel, layout.StackPanels ? 1 : 0);
        Grid.SetColumn(DiscoverPanel, layout.StackPanels ? 0 : 1);
        var headerWraps = layout.DiscoverWidth < 700;
        Grid.SetRow(TimeMachineHeaderActions, headerWraps ? 1 : 0);
        Grid.SetColumn(TimeMachineHeaderActions, headerWraps ? 0 : 1);
        Grid.SetColumnSpan(TimeMachineHeaderActions, headerWraps ? 2 : 1);
        TimeMachineHeader.RowSpacing = headerWraps ? 8 : 0;
        TimeMachineHeaderActions.HorizontalAlignment = headerWraps ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        TimeMachineHeaderActions.Orientation = layout.DiscoverWidth < 360 ? Orientation.Vertical : Orientation.Horizontal;
        TimeMachineHeaderActions.Spacing = layout.DiscoverWidth < 360 ? 4 : 12;
        TimeMachineLayout.MaximumRowsOrColumns = layout.Columns;
        TimeMachineLayout.MinItemWidth = DesktopCalendarLayout.TimeMachineMinimumItemWidth(layout.DiscoverWidth, layout.Columns);
        ViewModel.SetDiscoverCapacity(layout.Capacity);
    }

    private void OnDailyPickDescriptionHostSizeChanged(object sender, SizeChangedEventArgs args)
    {
        var lines = (int)(args.NewSize.Height / DailyPickDescription.LineHeight);
        DailyPickDescription.Opacity = lines > 0 ? 1 : 0;
        DailyPickDescription.MaxLines = Math.Max(1, lines);
    }

    private void UpdateDailyPickCover()
    {
        if (ViewModel.DailyPick?.Anime is { } anime)
            ManagedImageLoader.ConfigureCover(DailyPickCover, anime.ID, anime.CoverURL, 120);
        else ManagedImageLoader.Cancel(DailyPickCover);
    }

    private void UpdateDailyPickAction()
        => DailyPickWatchButton.Style = (Style)Application.Current.Resources[
            ViewModel.IsDailyPickWatching ? "DesktopButtonStyle" : "DesktopAccentButtonStyle"];

    private void OnFilterTextChanged(object sender, TextChangedEventArgs args)
        => ViewModel.SearchText = ((TextBox)sender).Text;
    private readonly DesktopSectionMotion _sectionMotion = new();
    private void OnAllScopeClick(object sender, RoutedEventArgs args) => SelectScope(false);
    private void OnMineScopeClick(object sender, RoutedEventArgs args) => SelectScope(true);
    private void SelectScope(bool mine)
    {
        var changed = ViewModel.ShowMineOnly != mine;
        ViewModel.ShowMineOnly = mine;
        UpdateScope();
        if (changed) _sectionMotion.Play(DayResults, backward: !mine);
    }
    private void UpdateScope()
    {
        AllScopeButton.IsChecked = !ViewModel.ShowMineOnly;
        MineScopeButton.IsChecked = ViewModel.ShowMineOnly;
    }
    private void OnSortSelectionChanged(object sender, SelectionChangedEventArgs args)
        => ViewModel.Sort = ((ComboBox)sender).SelectedIndex switch { 1 => CalendarSort.AirDate, 2 => CalendarSort.Title, _ => CalendarSort.Score };
    private void OnDayClick(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: CalendarDay day }) return;
        var previous = ViewModel.SelectedWeekday;
        var changed = day.Weekday != previous || ViewModel.IsFiltering;
        ViewModel.SelectDay(day.Weekday);
        if (changed) _sectionMotion.Play(DayResults, day.Weekday < previous);
    }

    private void OnAnimeCardClicked(object? sender, AnimeCardClickedEventArgs args)
        => OpenAnime(args.Anime);
    private void OnPickClick(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: Anime anime }) OpenAnime(anime);
    }
    private void OnDailyPickOpenClick(object sender, RoutedEventArgs args)
    {
        if (ViewModel.DailyPick?.Anime is { } anime) OpenAnime(anime);
    }
    private void OpenAnime(Anime anime) => _navigator.Navigate(typeof(AnimeDetailPage), anime.ID);

    private async void OnTrackingActionRequested(object? sender, AnimeCardTrackingActionEventArgs args)
        => await SaveTrackingAsync(args.Anime.ID, args.Status, fromTimeMachine: false);
    private async void OnDailyPickWatchClick(object sender, RoutedEventArgs args)
    {
        if (ViewModel.DailyPick?.Anime is { } anime) await SaveTrackingAsync(anime.ID, AnimeTrackingStatus.Watching, false);
    }
    private async void OnDailyPickFollowClick(object sender, RoutedEventArgs args)
    {
        if (ViewModel.DailyPick?.Anime is { } anime) await SaveTrackingAsync(anime.ID, AnimeTrackingStatus.Following, false);
    }
    private async void OnTimeMachinePlanClick(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: Anime anime }) await SaveTrackingAsync(anime.ID, AnimeTrackingStatus.PlanToWatch, true);
    }
    private async void OnTimeMachineFollowClick(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: Anime anime }) await SaveTrackingAsync(anime.ID, AnimeTrackingStatus.Following, true);
    }

    private async Task SaveTrackingAsync(int animeId, AnimeTrackingStatus status, bool fromTimeMachine)
    {
        var visit = _visit;
        try
        {
            if (fromTimeMachine) await ViewModel.ToggleTimeMachineStatusAsync(animeId, status);
            else await ViewModel.ToggleStatusAsync(animeId, status);
        }
        catch (Microsoft.Data.Sqlite.SqliteException error)
        {
            _logger.LogError(error, "放送日历标记保存失败，作品 {AnimeId}。", animeId);
            if (IsCurrent(visit)) ShowFailure("标记未能保存，请稍后重试。");
        }
    }

    private void OnTimeMachineYearClick(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: string text } && int.TryParse(text, out var years))
            ViewModel.TimeMachineYearsAgo = years;
        UpdateTimeMachineYears();
    }
    private void UpdateTimeMachineYears()
    {
        YearFiveButton.IsChecked = ViewModel.TimeMachineYearsAgo == 5;
        YearTenButton.IsChecked = ViewModel.TimeMachineYearsAgo == 10;
        YearTwentyButton.IsChecked = ViewModel.TimeMachineYearsAgo == 20;
    }
    private void OnTimeMachineOpenSeasonClick(object sender, RoutedEventArgs args)
        => _navigator.Navigate(typeof(PastSeasonPage), ViewModel.TimeMachineTarget);

    private void ShowFailure(string message)
    {
        if (DesktopFeedback.TryShow(message, error: true)) return;
        ErrorInfoBar.Message = message;
        ErrorInfoBar.IsOpen = true;
    }

    private void OnPickPointerEntered(object sender, PointerRoutedEventArgs args)
    {
        var row = (FrameworkElement)sender;
        _hoveredRows.Add(row);
        UpdatePickActions(row);
    }
    private void OnPickPointerExited(object sender, PointerRoutedEventArgs args)
    {
        var row = (FrameworkElement)sender;
        _hoveredRows.Remove(row);
        UpdatePickActions(row);
    }
    private void OnPickFocusChanged(object sender, RoutedEventArgs args)
    {
        var row = (FrameworkElement)sender;
        DispatcherQueue.TryEnqueue(() => { if (_active && row.IsLoaded) UpdatePickActions(row); });
    }
    private void UpdatePickActions(FrameworkElement row)
    {
        var visible = _hoveredRows.Contains(row);
        if (!visible && row.XamlRoot is { } root
            && FocusManager.GetFocusedElement(root) is Control { FocusState: FocusState.Keyboard } focused)
        {
            for (DependencyObject? current = focused; current is not null; current = VisualTreeHelper.GetParent(current))
                if (ReferenceEquals(current, row)) { visible = true; break; }
        }
        if (row.FindName("PickActions") is UIElement actions) actions.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (row.FindName("PickScore") is UIElement score) score.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
    }
    private void OnPickCoverLoaded(object sender, RoutedEventArgs args) => UpdatePickCover((Image)sender);
    private void OnPickCoverDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (sender.IsLoaded) UpdatePickCover((Image)sender);
    }
    private void OnPickCoverUnloaded(object sender, RoutedEventArgs args) => ManagedImageLoader.Cancel((Image)sender);
    private static void UpdatePickCover(Image image)
    {
        if (image.DataContext is Anime anime) ManagedImageLoader.ConfigureCover(image, anime.ID, anime.CoverURL, 40);
        else ManagedImageLoader.Cancel(image);
    }
}
